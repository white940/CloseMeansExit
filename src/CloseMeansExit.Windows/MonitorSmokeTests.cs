using System.Collections.Concurrent;
using System.Diagnostics;
using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

/// <summary>
/// Exercises real polling, notification delivery and rule persistence against a
/// private fixture, including blacklist rules applied to already-hidden instances.
/// No close-input observations are injected into the coordinator.
/// </summary>
internal static class MonitorSmokeTests
{
    internal static void Run(string fixtureExecutable, Action<string> report)
    {
        // A real UI message loop keeps async notification continuations on the
        // STA thread. DoEvents alone can uninstall its synchronization context
        // when the earlier standalone notice tests dispose their last form.
        Exception? failure = null;
        using var host = new SmokeHostForm();
        host.Shown += (_, _) => host.BeginInvoke(() =>
        {
            host.Hide();
            try
            {
                RunWithUiContext(fixtureExecutable, report);
                RunExistingBackgroundCases(fixtureExecutable, report);
            }
            catch (Exception error) { failure = error; }
            finally { host.Close(); }
        });
        Application.Run(host);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunWithUiContext(string fixtureExecutable, Action<string> report)
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), "CloseMeansExit-monitor-" + Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(dataDirectory, "settings.json");
        using var fixture = Process.Start(new ProcessStartInfo(fixtureExecutable, "--fixture")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(fixtureExecutable)!
        }) ?? throw new InvalidOperationException("监测链路测试进程启动失败。");
        _ = fixture.Handle;
        Process? protectedHelper = null;

        var platform = new WindowsPlatform();
        var incoming = new ConcurrentQueue<DiscoveryRecord>();
        var discoveries = new List<DiscoveryRecord>();
        var diagnostics = new ConcurrentQueue<string>();
        var rules = new List<AppRule>();
        using var monitor = new MonitorCoordinator(platform);
        using var notices = new ResidencyNotifications(platform,
            path => rules.FirstOrDefault(r => RulePaths.Equal(r.ExecutablePath, path)),
            () => monitor.Paused,
            (path, custom, children) =>
            {
                rules = [new AppRule { ExecutablePath = path, UseCustomCloseZone = custom, IncludeChildren = children }];
                SettingsStore.Save(settingsPath, new AppSettings { Rules = rules });
                monitor.SetRules(rules);
                return new NotificationSaveResult(true, "测试规则已保存。");
            },
            diagnostics.Enqueue);
        monitor.Discovered += incoming.Enqueue;
        monitor.Log += diagnostics.Enqueue;

        void Pump()
        {
            while (incoming.TryDequeue(out var record))
            {
                if (record.Observation.Process.Pid != fixture.Id) continue;
                discoveries.Add(record);
                notices.Enqueue(record);
            }
            Application.DoEvents();
        }

        ResidencyNoticeForm? Card() => Application.OpenForms.OfType<ResidencyNoticeForm>()
            .FirstOrDefault(f => !f.IsDisposed && f.Visible);

        try
        {
            nint window = 0;
            WaitFor(() => (window = platform.ListWindows().FirstOrDefault(w =>
                    w.Process.Pid == fixture.Id && w.Title.StartsWith(TestFixtureForm.TitlePrefix, StringComparison.Ordinal))?.Handle ?? 0) != 0,
                Pump, diagnostics, "等待监测链路测试窗口");
            ProcessIdentity identity = platform.TryGetIdentity(fixture.Id)
                ?? throw new InvalidOperationException("无法确认监测链路测试进程身份。");
            int helperPid = checked((int)Command(window, TestFixtureForm.SpawnProtectedHelperMessage));
            Process helperCandidate = Process.GetProcessById(helperPid);
            try
            {
                ProcessIdentity? helperIdentity = WindowsPlatform.ReadIdentity(helperCandidate.SafeHandle, helperCandidate.Id);
                Require(helperIdentity is not null &&
                    RulePaths.Equal(helperIdentity.ExecutablePath, Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) &&
                    helperIdentity.StartTimeUtcTicks >= identity.StartTimeUtcTicks &&
                    WindowsPlatform.TryReadProcessParents(out var parents) &&
                    parents.TryGetValue(helperPid, out int parentPid) && parentPid == fixture.Id,
                    "测试系统辅助进程身份不符合专用夹具预期，不能采纳该 PID。");
                protectedHelper = helperCandidate;
            }
            catch
            {
                helperCandidate.Dispose();
                throw;
            }
            Require(!protectedHelper.HasExited, "监测链路的系统辅助进程意外退出。");
            monitor.Start();
            report("CHECK: polling started with a protected system helper");

            // No input intent is supplied here: disappearance alone must still
            // produce an actionable candidate and must never authorize a kill.
            Command(window, 0x0112, 0xF060);
            WaitFor(() => Card() is not null, Pump, diagnostics, "窗口隐藏后未显示真实候选提醒");
            report("CHECK: real candidate card displayed");
            Require(discoveries.Any(r => r.Kind == DiscoveryKind.Candidate &&
                r.Observation.Evidence == CloseEvidence.WindowDisappeared), "未通过真实轮询发现后台驻留候选。");
            Require(!fixture.HasExited && !protectedHelper.HasExited, "首次候选发现错误地结束了测试进程。");
            var card = Card()!;
            Require(!card.InvokeRequired, "提醒必须在测试 UI 线程创建，不能跨线程操作按钮。");
            Descendants(card).OfType<Button>().Single(b => b.Text == "加入黑名单").PerformClick();
            report("CHECK: notification add button returned");
            Pump();
            AppSettings saved = SettingsStore.Load(settingsPath, out string? warning);
            Require(warning is null && saved.Rules.Count == 1 && saved.Rules[0].Enabled &&
                RulePaths.Equal(saved.Rules[0].ExecutablePath, identity.ExecutablePath), "提醒按钮没有保存正确的完整路径规则。");
            Require(!saved.Rules.Single().UseCustomCloseZone && !saved.Rules.Single().IncludeChildren,
                "后台自动清理不能依赖自绘关闭兼容或自动扩大子进程范围。");
            Require(!fixture.HasExited && !protectedHelper.HasExited && Card() is null,
                "加入规则应撤下卡片并先经过后台核验宽限期。");
            WaitFor(() => fixture.HasExited, Pump, diagnostics, "加入黑名单后未自动结束当前后台实例");
            WaitFor(() => discoveries.Any(r => r.Kind == DiscoveryKind.Terminated), Pump, diagnostics, "结束结果未送达管理界面");
            Require(discoveries.Any(r => r.Kind == DiscoveryKind.Terminated &&
                r.Observation.Evidence == CloseEvidence.BlacklistedBackground), "自动结束没有使用新的黑名单后台核验事件。");
            // The stdin-wait helper can exit naturally when its parent closes
            // the pipe. Root-only scope is verified by the saved rule above;
            // independent child preservation is covered by native smoke tests.
            Require(discoveries.Count(r => r.Kind == DiscoveryKind.Candidate) == 1 && Card() is null,
                "加入黑名单后的后台清理不应再次弹出兼容修复或加入提醒。");
            report("PASS: notification saves the rule and ends the already-hidden instance without another close input");
        }
        catch (Exception error)
        {
            report("FAIL: monitor pipeline before cleanup: " + error);
            throw;
        }
        finally
        {
            monitor.SetPaused(true);
            notices.Reset();
            if (!fixture.HasExited)
            {
                fixture.Kill();
                fixture.WaitForExit(3000);
            }
            if (protectedHelper is not null)
            {
                if (!protectedHelper.HasExited && !protectedHelper.WaitForExit(2000))
                {
                    protectedHelper.Kill();
                    protectedHelper.WaitForExit(2000);
                }
                protectedHelper.Dispose();
            }
            string fullDirectory = Path.GetFullPath(dataDirectory);
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "CloseMeansExit-monitor-");
            if (fullDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullDirectory))
                Directory.Delete(fullDirectory, true);
        }
    }

    private static void RunExistingBackgroundCases(string fixtureExecutable, Action<string> report)
    {
        var fixtures = new List<Process>();
        var platform = new WindowsPlatform();
        var diagnostics = new ConcurrentQueue<string>();
        var discoveries = new ConcurrentQueue<DiscoveryRecord>();
        using var monitor = new MonitorCoordinator(platform);
        monitor.Log += diagnostics.Enqueue;
        monitor.Discovered += discoveries.Enqueue;
        void Pump() => Application.DoEvents();
        int BackgroundObservations() => diagnostics.Count(line => line.Contains("发现黑名单后台进程", StringComparison.Ordinal));
        Process StartFixture()
        {
            var process = Process.Start(new ProcessStartInfo(fixtureExecutable, "--fixture")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(fixtureExecutable)!
            }) ?? throw new InvalidOperationException("后台规则测试夹具启动失败。");
            fixtures.Add(process);
            _ = process.Handle;
            return process;
        }
        nint FindFixtureWindow(Process process)
        {
            nint window = 0;
            WaitFor(() => (window = platform.ListWindows().FirstOrDefault(w => w.Process.Pid == process.Id &&
                    w.Title.StartsWith(TestFixtureForm.TitlePrefix, StringComparison.Ordinal))?.Handle ?? 0) != 0,
                Pump, diagnostics, "等待后台规则测试窗口");
            return window;
        }
        void ObserveFor(double seconds)
        {
            var elapsed = Stopwatch.StartNew();
            WaitFor(() => elapsed.Elapsed.TotalSeconds >= seconds, Pump, diagnostics, "等待后台规则观察周期");
        }

        try
        {
            Process alreadyHidden = StartFixture();
            Process visible = StartFixture();
            Process minimized = StartFixture();
            nint hiddenWindow = FindFixtureWindow(alreadyHidden);
            nint visibleWindow = FindFixtureWindow(visible);
            nint minimizedWindow = FindFixtureWindow(minimized);
            ProcessIdentity identity = platform.TryGetIdentity(alreadyHidden.Id)
                ?? throw new InvalidOperationException("无法确认后台规则测试夹具身份。");
            AppRule rule = new() { ExecutablePath = identity.ExecutablePath };
            Command(hiddenWindow, 0x0112, 0xF060);
            Command(minimizedWindow, TestFixtureForm.MinimizeMessage);
            Require(!NativeMethods.IsWindowVisible(hiddenWindow) && NativeMethods.IsIconic(minimizedWindow),
                "启动监测前必须已有隐藏实例和最小化实例。");
            monitor.SetRules([rule]);
            monitor.Start();
            WaitFor(() => alreadyHidden.HasExited, Pump, diagnostics, "启动监测前已隐藏的黑名单实例未被发现并结束");
            Require(!visible.HasExited && !minimized.HasExited && NativeMethods.IsWindowVisible(visibleWindow) &&
                NativeMethods.IsIconic(minimizedWindow), "同路径的可见和最小化实例必须分别受到保护。");
            Require(discoveries.Any(r => r.Observation.Process.Pid == alreadyHidden.Id &&
                r.Kind == DiscoveryKind.Terminated && r.Observation.Evidence == CloseEvidence.BlacklistedBackground),
                "启动前后台实例必须由后台扫描自动发现，不能依赖窗口消失基线。");
            report("PASS: startup discovers an already-hidden instance while same-path visible and minimized instances stay alive");

            monitor.SetPaused(true);
            Command(visibleWindow, 0x0112, 0xF060);
            ObserveFor(3.4);
            Require(!visible.HasExited, "暂停期间不得清理新隐藏的黑名单实例。");
            int observedBeforeResume = BackgroundObservations();
            monitor.SetPaused(false);
            WaitFor(() => BackgroundObservations() > observedBeforeResume, Pump, diagnostics,
                "恢复监测后没有重新观察现有后台实例");
            Require(!visible.HasExited, "恢复监测必须重新等待宽限期，不能重放旧结束操作。");
            monitor.SetRules([]);
            ObserveFor(3.4);
            Require(!visible.HasExited, "宽限期内删除规则必须取消待结束操作。");

            monitor.SetRules([rule with { Enabled = false }]);
            ObserveFor(2.4);
            Require(!visible.HasExited, "已禁用的黑名单规则不得清理现有后台实例。");
            monitor.SetRules([rule]);
            WaitFor(() => visible.HasExited, Pump, diagnostics, "重新启用规则后没有从新观察自动清理后台实例");
            Require(!minimized.HasExited, "另一实例被自动清理时，最小化实例仍必须保留。");
            report("PASS: pause and rule removal cancel pending cleanup; enabling a rule starts a fresh background check");

            Command(minimizedWindow, TestFixtureForm.RestoreMessage);
            Require(Command(minimizedWindow, TestFixtureForm.DelayedHideMessage) == 1, "无法安排延迟隐藏夹具。");
            ObserveFor(3.4);
            Require(!minimized.HasExited && NativeMethods.IsWindowVisible(minimizedWindow),
                "延迟隐藏的原窗口可见期间必须保留进程。");
            WaitFor(() => minimized.HasExited, Pump, diagnostics, "延迟隐藏后未自动结束后台实例");
            report("PASS: delayed hiding is handled without a captured click or a custom-close permission");
        }
        catch (Exception error)
        {
            report("FAIL: background-rule pipeline before cleanup: " + error);
            throw;
        }
        finally
        {
            monitor.SetPaused(true);
            foreach (Process fixture in fixtures)
            {
                try
                {
                    if (!fixture.HasExited)
                    {
                        fixture.Kill();
                        fixture.WaitForExit(3000);
                    }
                }
                finally { fixture.Dispose(); }
            }
        }
    }

    private static void WaitFor(Func<bool> ready, Action pump, ConcurrentQueue<string> diagnostics, string step)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            pump();
            if (ready()) return;
            if (clock.Elapsed > TimeSpan.FromSeconds(12))
                throw new TimeoutException(step + "。" + string.Join(" | ", diagnostics));
            Thread.Sleep(30);
        } while (true);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static nuint Command(nint window, uint message, nuint wParam = 0)
    {
        Require(NativeMethods.SendMessageTimeout(window, message, wParam, 0, NativeMethods.SmtoAbortIfHung, 3000, out nuint result) != 0,
            $"监测链路测试消息 0x{message:X} 失败。");
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SmokeHostForm : Form
    {
        internal SmokeHostForm()
        {
            ShowInTaskbar = false;
            Opacity = 0;
            Size = new Size(1, 1);
            FormBorderStyle = FormBorderStyle.None;
        }

        protected override bool ShowWithoutActivation => true;
    }
}
