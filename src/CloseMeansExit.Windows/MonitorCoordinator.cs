using System.Collections.Concurrent;
using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

public enum DiscoveryKind { Candidate, Terminated, TerminationFailed }
public sealed record DiscoveryRecord(CloseObservation Observation, long WorkingSetBytes, string Status,
    DiscoveryKind Kind = DiscoveryKind.Candidate);

internal sealed class MonitorCoordinator : IDisposable
{
    private readonly WindowsPlatform _platform;
    private readonly CloseInputMonitor _input;
    private readonly ConcurrentQueue<(CloseObservation Observation, AppRule? Rule, long Version)> _queue = new();
    private readonly Dictionary<ProcessIdentity, (CloseSession Session, long Version)> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly object _ruleLock = new();
    private Dictionary<string, (AppRule Rule, DateTimeOffset Since, long Version)> _rules = new(StringComparer.OrdinalIgnoreCase);
    private long _nextVersion;
    private DateTimeOffset _acceptObservationsSince = DateTimeOffset.UtcNow;
    private Dictionary<ProcessIdentity, WindowEntry> _previous = [];
    private volatile bool _paused;
    private int _reset;
    private Task? _worker;
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;

    public event Action<DiscoveryRecord>? Discovered;
    public event Action<string>? Log;

    public MonitorCoordinator(WindowsPlatform platform)
    {
        _platform = platform;
        _input = new CloseInputMonitor(platform, FindRule);
        _input.Observed += OnObservation;
        _input.Diagnostic += message => Log?.Invoke(message);
    }

    public bool Paused => _paused;

    public void SetPaused(bool paused)
    {
        lock (_ruleLock)
        {
            _paused = paused;
            _acceptObservationsSince = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _reset, 1);
        }
        Log?.Invoke(paused ? "已暂停监测；所有待执行操作已取消。" : "已恢复监测。规则从新的关闭操作开始生效。");
    }

    public void SetRules(IEnumerable<AppRule> rules)
    {
        lock (_ruleLock)
        {
            var replacement = new Dictionary<string, (AppRule, DateTimeOffset, long)>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                string path = RulePaths.Normalize(rule.ExecutablePath);
                if (path.Length == 0) continue;
                bool unchanged = _rules.TryGetValue(path, out var old) && old.Rule == rule;
                replacement[path] = unchanged ? old : (rule, DateTimeOffset.UtcNow, ++_nextVersion);
            }
            _rules = replacement;
        }
    }

    public AppRule? FindRule(string path)
    {
        lock (_ruleLock)
        {
            return _rules.GetValueOrDefault(RulePaths.Normalize(path)).Rule;
        }
    }

    public void Start()
    {
        // Capture already-open windows before reporting that monitoring is
        // ready, so a close immediately after startup is not lost by polling.
        _previous = _platform.ListWindows().GroupBy(w => w.Process).ToDictionary(g => g.Key, g => g.First());
        _lastScan = DateTimeOffset.UtcNow;
        _input.Start();
        _worker = Task.Run(RunAsync);
        Log?.Invoke("监测已启动。发现未加入黑名单的后台驻留应用时会提醒确认，首次发现不会结束进程。");
    }

    internal void OnObservation(CloseObservation observation)
    {
        AppRule? captured = null;
        lock (_ruleLock)
        {
            if (_paused || observation.At < _acceptObservationsSince) return;
            long version = 0;
            if (_rules.TryGetValue(RulePaths.Normalize(observation.Process.ExecutablePath), out var entry)
                && entry.Since <= observation.At)
            {
                captured = entry.Rule;
                version = entry.Version;
            }
            _queue.Enqueue((observation, captured, version));
        }
        Log?.Invoke($"{Path.GetFileName(observation.Process.ExecutablePath)}：收到{observation.Evidence switch
        {
            CloseEvidence.StandardCloseButton => "标准关闭按钮",
            CloseEvidence.AltF4 => "Alt+F4",
            CloseEvidence.CustomTitleBarZone => "自绘关闭区域",
            _ => "窗口消失"
        }}信号，正在核验是否仍驻留。");
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try { Tick(); }
                catch (Exception e)
                {
                    _pending.Clear();
                    _previous.Clear();
                    while (_queue.TryDequeue(out _)) { }
                    Log?.Invoke($"本轮监测及待执行操作已取消：{e.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _reset, 0) != 0)
        {
            _pending.Clear();
            _previous.Clear();
            while (_queue.TryDequeue(out _)) { }
        }
        if (_paused) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastScan >= TimeSpan.FromSeconds(2))
        {
            // Only loss of all visible windows creates a weak observation. It never authorizes a kill.
            var current = _platform.ListWindows()
                .GroupBy(w => w.Process).ToDictionary(g => g.Key, g => g.First());
            foreach (var pair in _previous)
            {
                if (!current.ContainsKey(pair.Key) && !_pending.ContainsKey(pair.Key))
                    OnObservation(new CloseObservation(pair.Key, pair.Value.Title, CloseEvidence.WindowDisappeared, now));
            }
            _previous = current;
            _lastScan = now;
        }

        while (_queue.TryDequeue(out var item))
        {
            if (_pending.TryGetValue(item.Observation.Process, out var old))
            {
                if (old.Session.Observation.Evidence != CloseEvidence.WindowDisappeared
                    || item.Observation.Evidence == CloseEvidence.WindowDisappeared) continue;
            }
            _pending[item.Observation.Process] = (new CloseSession(item.Observation, item.Rule), item.Version);
        }

        foreach (var pair in _pending.ToArray())
        {
            var session = pair.Value.Session;
            bool children = session.CapturedRule?.IncludeChildren == true;
            var snapshot = _platform.Probe(pair.Key, children, session.Observation.SourceWindow);
            AppRule? rule;
            lock (_ruleLock)
            {
                rule = _rules.TryGetValue(RulePaths.Normalize(pair.Key.ExecutablePath), out var active)
                    && active.Version == pair.Value.Version && active.Since <= session.Observation.At
                    ? active.Rule : null;
            }
            var decision = session.Advance(snapshot, rule, DateTimeOffset.UtcNow, _paused);
            if (decision.Kind == DecisionKind.Wait) continue;
            _pending.Remove(pair.Key);
            if (decision.Kind == DecisionKind.Ignore)
            {
                if (session.CapturedRule?.Enabled == true ||
                    (snapshot.IsAlive && (!snapshot.Reliable || !snapshot.IdentityMatches ||
                        session.Observation.Evidence != CloseEvidence.WindowDisappeared)))
                    Log?.Invoke($"{Path.GetFileName(pair.Key.ExecutablePath)}：{decision.Reason}");
                continue;
            }
            if (decision.Kind == DecisionKind.Candidate)
            {
                string status = session.Observation.Evidence == CloseEvidence.WindowDisappeared
                    ? "疑似后台驻留（窗口消失）" : "点关闭后仍驻留";
                Discovered?.Invoke(new DiscoveryRecord(session.Observation, snapshot.WorkingSetBytes, status));
                continue;
            }

            // Recheck the current rule and pause flag immediately before the guarded native operation.
            bool ended;
            string error;
            lock (_ruleLock)
            {
                // The same gate makes pause/rule changes a boundary: after they return,
                // an already invalidated request cannot begin native termination.
                if (_paused || session.Observation.At < _acceptObservationsSince
                    || !_rules.TryGetValue(RulePaths.Normalize(pair.Key.ExecutablePath), out var active)
                    || active.Version != pair.Value.Version || active.Rule != rule) continue;
                ended = _platform.TryTerminate(pair.Key, children, out error);
            }
            string result = ended ? "已按黑名单结束" : $"未结束：{error}";
            Discovered?.Invoke(new DiscoveryRecord(session.Observation, snapshot.WorkingSetBytes, result,
                ended ? DiscoveryKind.Terminated : DiscoveryKind.TerminationFailed));
            Log?.Invoke($"{Path.GetFileName(pair.Key.ExecutablePath)}：{result}");
        }
    }

    public void Dispose()
    {
        lock (_ruleLock) { _paused = true; }
        _stop.Cancel();
        _input.Dispose();
        _worker?.Wait(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
