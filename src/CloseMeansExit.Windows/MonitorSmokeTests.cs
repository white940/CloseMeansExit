using System.Collections.Concurrent;
using System.Diagnostics;
using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

/// <summary>
/// Exercises real polling, notification delivery and rule persistence against a
/// private fixture. The second close intent is injected at the coordinator;
/// physical mouse/keyboard hooks still require a separate desktop acceptance test.
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
            try { RunWithUiContext(fixtureExecutable, report); }
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
            protectedHelper = Process.GetProcessById(helperPid);
            _ = protectedHelper.Handle;
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
            Require(!fixture.HasExited && !protectedHelper.HasExited && Card() is null,
                "加入规则应撤下卡片且保留当前进程及辅助进程。");

            Command(window, TestFixtureForm.RestoreMessage);
            var observedAgain = Stopwatch.StartNew();
            WaitFor(() => observedAgain.Elapsed > TimeSpan.FromMilliseconds(2300), Pump, diagnostics, "等待恢复窗口进入轮询基线");
            Command(window, 0x0112, 0xF060);
            WaitFor(() => Card() is not null, Pump, diagnostics, "已有规则但缺少关闭信号时没有兼容提示");
            var repair = Card()!;
            Descendants(repair).OfType<Button>().Single(b => b.Text == "启用关闭按钮兼容").PerformClick();
            Pump();
            saved = SettingsStore.Load(settingsPath, out warning);
            Require(warning is null && saved.Rules.Single().UseCustomCloseZone && !fixture.HasExited,
                "启用兼容应保存未来规则，不能追溯结束当前候选。");
            report("CHECK: existing-rule compatibility repair saved without retroactive termination");

            Command(window, TestFixtureForm.RestoreMessage);
            var source = new CloseWindowSource(window, identity);
            WaitFor(() => platform.Probe(identity, false, source) is { HasVisibleWindows: true, Reliable: true, OnlySourceWindowVisible: true },
                Pump, diagnostics, "测试窗口未恢复");
            monitor.OnObservation(new CloseObservation(identity, "Fixture", CloseEvidence.CustomTitleBarZone, DateTimeOffset.UtcNow)
            {
                SourceWindow = source
            });
            var delayedClose = Stopwatch.StartNew();
            Require(Command(window, TestFixtureForm.DelayedHideMessage) == 1, "未能安排测试原窗口延迟隐藏。");
            report("CHECK: one source-bound close intent queued; original window stays visible for 4.5 seconds");
            WaitFor(() => !NativeMethods.IsWindowVisible(window), Pump, diagnostics, "原关闭窗口未按计划延迟隐藏");
            Require(delayedClose.Elapsed >= TimeSpan.FromMilliseconds(TestFixtureForm.DelayedHideMilliseconds - 300),
                "测试原窗口必须在三秒宽限期结束后才隐藏。");
            WaitFor(() => fixture.HasExited, Pump, diagnostics, "已有规则的下一次关闭未结束测试实例");
            WaitFor(() => discoveries.Any(r => r.Kind == DiscoveryKind.Terminated), Pump, diagnostics, "结束结果未送达管理界面");
            report("PASS: source-bound close intent survives delayed hiding beyond the three-second grace period");
            Require(discoveries.Count(r => r.Kind == DiscoveryKind.Candidate) == 2 && Card() is null,
                "已有规则的关闭不应再次弹出加入提醒。");
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
