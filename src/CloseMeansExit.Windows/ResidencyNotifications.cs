using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

internal sealed record NotificationSaveResult(bool Success, string Message);

/// <summary>
/// UI-thread owner of actionable notices. Deferred candidates are revalidated;
/// pause/exit invalidates both displayed forms and in-flight async probes.
/// </summary>
internal sealed class ResidencyNotifications : IDisposable
{
    private readonly WindowsPlatform _platform;
    private readonly Func<string, AppRule?> _findRule;
    private readonly Func<bool> _isPaused;
    private readonly Func<string, bool, bool, NotificationSaveResult> _saveRule;
    private readonly Action<string> _log;
    private readonly NotificationQueue _queue = new();
    private readonly System.Windows.Forms.Timer _activeCheck = new() { Interval = 2000 };
    private ResidencyNoticeForm? _activeForm;
    private ResidencyNotice? _activeNotice;
    private DateTimeOffset _acceptSince = DateTimeOffset.UtcNow;
    private long _generation;
    private bool _selecting;
    private bool _checking;
    private bool _closingCurrent;
    private bool _disposed;

    public ResidencyNotifications(WindowsPlatform platform, Func<string, AppRule?> findRule,
        Func<bool> isPaused, Func<string, bool, bool, NotificationSaveResult> saveRule, Action<string> log)
    {
        _platform = platform;
        _findRule = findRule;
        _isPaused = isPaused;
        _saveRule = saveRule;
        _log = log;
        _activeCheck.Tick += async (_, _) => await CheckActiveAsync();
    }

    public void Enqueue(DiscoveryRecord record)
    {
        if (_disposed || _isPaused() || record.Kind != DiscoveryKind.Candidate || record.Observation.At < _acceptSince) return;
        string path = record.Observation.Process.ExecutablePath;
        if (!CanOffer(record.Observation))
        {
            _log($"{Path.GetFileName(path)}：已在启用的黑名单中，将按后台状态自动处理。");
            return;
        }
        // A card already asking about this path is sufficient. Future close
        // events are eligible again as soon as that card is dismissed.
        if (_activeNotice is { } active && RulePaths.Equal(active.Observation.Process.ExecutablePath, path)) return;
        _queue.Enqueue(new ResidencyNotice(record.Observation, record.WorkingSetBytes));
        _activeForm?.SetPendingCount(_queue.Count);
        _ = ShowNextAsync();
    }

    public void Reset()
    {
        _generation++;
        _acceptSince = DateTimeOffset.UtcNow;
        _queue.Clear();
        _activeCheck.Stop();
        var form = _activeForm;
        _activeForm = null;
        _activeNotice = null;
        // Clearing references first prevents FormClosed from advancing the queue.
        if (form is not null && !form.IsDisposed) form.Close();
    }

    public void RulesChanged()
    {
        if (_activeNotice is { } notice && !CanOffer(notice.Observation)) CloseCurrent();
    }

    private bool CanOffer(CloseObservation observation)
    {
        AppRule? rule = _findRule(observation.Process.ExecutablePath);
        return rule?.Enabled != true;
    }

    private async Task ShowNextAsync()
    {
        if (_disposed || _isPaused() || _selecting || _activeForm is not null) return;
        _selecting = true;
        long generation = _generation;
        try
        {
            while (_queue.TryDequeue(out var notice))
            {
                if (_disposed || _isPaused() || generation != _generation) return;
                string path = notice.Observation.Process.ExecutablePath;
                if (notice.Observation.At < _acceptSince || !CanOffer(notice.Observation)) continue;
                ProcessSnapshot snapshot;
                try { snapshot = await Task.Run(() => _platform.Probe(notice.Observation.Process, false)); }
                catch (Exception e) { if (!_disposed) _log($"提醒前状态核验失败：{e.Message}"); continue; }
                if (_disposed || _isPaused() || generation != _generation) return;
                if (!Valid(snapshot) || !CanOffer(notice.Observation)) continue;

                var current = notice with { WorkingSetBytes = snapshot.WorkingSetBytes };
                var popup = new ResidencyNoticeForm(current.Observation, current.WorkingSetBytes,
                    _findRule(path) is { Enabled: false },
                    _findRule(path)?.IncludeChildren == true);
                _activeNotice = current;
                _activeForm = popup;
                popup.SetPendingCount(_queue.Count);
                popup.AddRequested += () => AddFromNotice(popup, current);
                popup.DismissRequested += () =>
                {
                    if (_disposed || _closingCurrent || !ReferenceEquals(_activeForm, popup)) return;
                    _queue.Remove(path);
                    _log($"{Path.GetFileName(path)}：本次暂不处理，下次关闭后仍可提醒。");
                };
                popup.FormClosed += (_, _) =>
                {
                    if (!ReferenceEquals(_activeForm, popup)) return;
                    _activeForm = null;
                    _activeNotice = null;
                    _activeCheck.Stop();
                    _ = ShowNextAsync();
                };
                // The manager window may be hidden. No owner relationship is
                // used, so hiding it never hides the actionable notification.
                popup.Show();
                _activeCheck.Start();
                return;
            }
        }
        catch (Exception e)
        {
            if (!_disposed) _log($"无法显示驻留提醒：{e.Message}");
            CloseCurrent();
        }
        finally
        {
            _selecting = false;
            if (!_disposed && !_isPaused() && _activeForm is null && _queue.Count > 0) _ = ShowNextAsync();
        }
    }

    private void AddFromNotice(ResidencyNoticeForm popup, ResidencyNotice notice)
    {
        if (_disposed || _isPaused() || !ReferenceEquals(_activeForm, popup)) return;
        popup.SetBusy(true);
        try
        {
            string path = notice.Observation.Process.ExecutablePath;
            var result = _saveRule(path, popup.UseCustomCloseZone, popup.IncludeChildren);
            if (result.Success)
            {
                _queue.Remove(path);
                // The coordinator applies the saved rule to current and future
                // background instances after revalidating their state.
                CloseCurrent();
            }
            else
            {
                popup.SetBusy(false);
                popup.ShowSaveFailure(result.Message);
            }
        }
        catch (Exception e)
        {
            popup.SetBusy(false);
            popup.ShowSaveFailure(e.Message);
        }
    }

    private async Task CheckActiveAsync()
    {
        if (_disposed || _checking || _activeNotice is not { } notice || _activeForm is not { } popup) return;
        _checking = true;
        long generation = _generation;
        try
        {
            var snapshot = await Task.Run(() => _platform.Probe(notice.Observation.Process, false));
            if (!_disposed && generation == _generation && ReferenceEquals(_activeForm, popup)
                && (_isPaused() || !Valid(snapshot) || !CanOffer(notice.Observation))) CloseCurrent();
        }
        catch (Exception)
        {
            if (!_disposed && generation == _generation && ReferenceEquals(_activeForm, popup)) CloseCurrent();
        }
        finally { _checking = false; }
    }

    private static bool Valid(ProcessSnapshot snapshot) => snapshot is
        { IsAlive: true, IdentityMatches: true, HasVisibleWindows: false, Reliable: true };

    private void CloseCurrent()
    {
        // FormClosed owns normal queue advancement. During Reset/Dispose the
        // references were cleared first, so it cannot resurrect a notification.
        if (_activeForm is not { IsDisposed: false } popup) return;
        try
        {
            _closingCurrent = true;
            popup.Close();
        }
        finally { _closingCurrent = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        _activeCheck.Dispose();
    }
}
