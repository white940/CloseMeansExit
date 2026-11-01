using System.Diagnostics;
using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

/// <summary>
/// Windows-only integration checks. Termination targets are exclusively fixture
/// processes created by this invocation, never an arbitrary running application.
/// </summary>
internal static class SmokeTests
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "CloseMeansExit-smoke.log");

    internal static int Run()
    {
        var log = new List<string> { $"Started {DateTimeOffset.UtcNow:O}" };
        void Record(string line)
        {
            log.Add(line);
            File.WriteAllLines(LogPath, log);
        }
        Record("CHECK: notification UI smoke tests");
        var fixtures = new List<Process>();
        var clock = Stopwatch.StartNew();
        string fixtureDirectory = Path.Combine(Path.GetTempPath(), "CloseMeansExit-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            NotificationSmokeTests.Run();
            Record("PASS: actionable residency notification content and choices");
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("缺少应用程序路径。");
            Require(!string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase),
                "请运行 CloseMeansExit.exe --smoke-test，而不是 dotnet CloseMeansExit.dll。");

            // The production adapter protects its own executable path, including
            // other instances. A separate, renamed copy exercises real process
            // termination without weakening that protection for tests.
            Directory.CreateDirectory(fixtureDirectory);
            string fixtureExecutable = Path.Combine(fixtureDirectory, "CloseMeansExit.Fixture.exe");
            CopyFixturePayload(AppContext.BaseDirectory, executable, fixtureDirectory, Path.GetFileName(fixtureExecutable));
            var platform = new WindowsPlatform();
            var start = new ProcessStartInfo(fixtureExecutable) { UseShellExecute = false, WorkingDirectory = fixtureDirectory };
            start.ArgumentList.Add("--fixture");
            Process fixture = Process.Start(start) ?? throw new InvalidOperationException("测试进程启动失败。");
            fixtures.Add(fixture);
            _ = fixture.Handle;

            nint primary = WaitFor(() => FindWindow(platform, fixture.Id), h => h != 0, clock, "等待测试窗口");
            ProcessIdentity identity = platform.TryGetIdentity(fixture.Id) ?? throw new InvalidOperationException("无法读取测试进程身份。");
            var source = new CloseWindowSource(primary, identity);
            Require(platform.Probe(identity, false) is { IsAlive: true, IdentityMatches: true, HasVisibleWindows: true, Reliable: true }, "可见窗口探测失败。");
            Require(platform.Probe(identity, false, source) is { HasVisibleWindows: true, Reliable: true, OnlySourceWindowVisible: true },
                "唯一的原关闭窗口应允许继续等待隐藏。");
            Require(!platform.Probe(identity, false).OnlySourceWindowVisible &&
                !platform.Probe(identity, false, source with { Owner = identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 } }).OnlySourceWindowVisible &&
                !platform.Probe(identity, false, source with { Owner = identity with { ExecutablePath = identity.ExecutablePath + ".wrong.exe" } }).OnlySourceWindowVisible,
                "缺少或伪造的原窗口身份不能延长等待。");
            Require(!platform.TryTerminate(identity, false, out _), "可见窗口保护失效。");
            Record("PASS: sole source window permits observation only; visible window still prevents termination");

            Require(!platform.TryTerminate(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 }, false, out _), "创建时间不匹配保护失效。");
            Require(!platform.TryTerminate(identity with { ExecutablePath = identity.ExecutablePath + ".wrong.exe" }, false, out _), "路径不匹配保护失效。");
            Require(platform.Probe(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 }, false).IdentityMatches == false, "探测未识别创建时间不匹配。");
            var own = new ProcessIdentity(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, executable);
            Require(!platform.TryTerminate(own, false, out _), "工具自身保护失效。");
            Require(!fixture.HasExited, "身份不匹配测试意外结束了fixture。");
            Record("PASS: PID/start-time/path and self protections");

            Command(primary, TestFixtureForm.MinimizeMessage);
            WaitFor(() => NativeMethods.IsIconic(primary), minimized => minimized, clock, "检查最小化窗口");
            Require(platform.Probe(identity, false, source) is { HasVisibleWindows: true, Reliable: true, OnlySourceWindowVisible: false },
                "最小化的原窗口不能延长等待。");
            Require(!platform.TryTerminate(identity, false, out _), "最小化窗口保护失效。");
            Command(primary, TestFixtureForm.RestoreMessage);
            Record("PASS: minimized window remains protected");

            Command(primary, TestFixtureForm.CreateSecondaryMessage);
            WaitFor(() => platform.ListWindows().Where(w => w.Process.Pid == fixture.Id).ToArray(), a => a.Length >= 2, clock, "等待第二窗口");
            Require(platform.Probe(identity, false, source) is { HasVisibleWindows: true, Reliable: true, OnlySourceWindowVisible: false },
                "第二窗口存在时不得将原窗口视为唯一可见窗口。");
            CloseWindow(primary);
            Require(!fixture.HasExited && NativeMethods.IsWindow(primary), "关闭操作应仅隐藏测试主窗口，不能销毁窗口或退出进程。");
            Require(platform.Probe(identity, false).HasVisibleWindows, "第二窗口未被保护。");
            Require(!platform.TryTerminate(identity, false, out _), "第二窗口存在时误结束进程。");
            foreach (WindowEntry entry in platform.ListWindows().Where(w => w.Process.Pid == fixture.Id))
            {
                CloseWindow(entry.Handle);
            }

            WaitFor(() => platform.Probe(identity, false), s => s is { IsAlive: true, IdentityMatches: true, HasVisibleWindows: false, Reliable: true }, clock, "等待全部窗口隐藏");
            Record("PASS: closing only one of multiple windows is protected");

            nuint protectedPid = Command(primary, TestFixtureForm.SpawnProtectedHelperMessage);
            Require(protectedPid > 0 && protectedPid <= int.MaxValue, "测试系统子进程PID无效。");
            Process protectedHelper = Process.GetProcessById((int)protectedPid);
            _ = protectedHelper.Handle;
            fixtures.Add(protectedHelper);
            ProcessIdentity protectedIdentity = platform.TryGetIdentity(protectedHelper.Id)
                ?? throw new InvalidOperationException("无法确认测试系统子进程身份。");
            Require(RulePaths.Equal(protectedIdentity.ExecutablePath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
                && protectedIdentity.StartTimeUtcTicks >= identity.StartTimeUtcTicks,
                "测试系统子进程身份不符合预期。");
            Require(platform.Probe(identity, false) is { IsAlive: true, IdentityMatches: true, HasVisibleWindows: false, Reliable: true },
                "无窗口的系统目录子进程不应阻止只读驻留探测。");
            Require(platform.Probe(identity, true) is { IsAlive: true, IdentityMatches: true, HasVisibleWindows: false, Reliable: true },
                "包含子进程的只读探测不应因系统目录子进程失效。");
            Require(!platform.TryTerminate(identity, true, out _), "包含系统目录子进程时不应允许结束进程树。");
            Require(!fixture.HasExited && !protectedHelper.HasExited, "系统目录保护测试意外结束了测试进程。");
            Command(primary, TestFixtureForm.CleanupProtectedHelperMessage);
            Require(protectedHelper.WaitForExit(5000), "测试系统子进程未按要求清理。");
            Record("PASS: system-directory helper permits read-only discovery but prevents tree termination");

            Command(primary, TestFixtureForm.RestoreMessage);
            Require(NativeMethods.PostMessage(primary, TestFixtureForm.SaveDialogWithOwnerMessage, 0, 0), "无法显示测试保存提示。");
            nint dialog = WaitFor(() => platform.ListWindows().FirstOrDefault(w => w.Process.Pid == fixture.Id && w.Title == TestFixtureForm.SaveTitle)?.Handle ?? 0,
                h => h != 0, clock, "等待保存提示");
            Require(NativeMethods.IsWindowVisible(primary) && !NativeMethods.IsWindowEnabled(primary),
                "保存提示必须保持原窗口可见并禁用其输入。");
            Require(platform.Probe(identity, false, source) is { HasVisibleWindows: true, Reliable: true, OnlySourceWindowVisible: false },
                "保存提示和禁用的原窗口不能延长等待。");
            Require(!platform.TryTerminate(identity, false, out _), "保存对话框保护失效。");
            CloseWindow(dialog);
            WaitFor(() => NativeMethods.IsWindowEnabled(primary), enabled => enabled, clock, "等待保存提示解除输入禁用");
            Require(platform.Probe(identity, false, source).OnlySourceWindowVisible, "保存提示关闭后应重新识别唯一原窗口。");
            CloseWindow(primary);
            WaitFor(() => platform.Probe(identity, false), s => !s.HasVisibleWindows && s.Reliable, clock, "等待保存提示关闭");
            Record("PASS: visible save dialog prevents termination");

            nuint childPid = Command(primary, TestFixtureForm.SpawnHelperMessage);
            Require(childPid > 0 && childPid <= int.MaxValue, "测试子进程PID无效。");
            Process child = Process.GetProcessById((int)childPid);
            _ = child.Handle; // Retain this exact process object for cleanup.
            fixtures.Add(child);
            nint childWindow = WaitFor(() => FindWindow(platform, child.Id), h => h != 0, clock, "等待测试子进程窗口");
            ProcessIdentity childIdentity = platform.TryGetIdentity(child.Id) ?? throw new InvalidOperationException("无法确认测试子进程身份。");
            Require(RulePaths.Equal(childIdentity.ExecutablePath, identity.ExecutablePath) && childIdentity.StartTimeUtcTicks >= identity.StartTimeUtcTicks,
                "测试子进程身份不符合预期。");
            Require(child.StartTime.ToUniversalTime().Ticks == childIdentity.StartTimeUtcTicks, "测试子进程句柄身份不符合预期。");
            Require(platform.Probe(identity, false, new CloseWindowSource(childWindow, childIdentity)) is
                { HasVisibleWindows: true, Reliable: true, OnlySourceWindowVisible: true },
                "子进程实际持有的唯一原窗口应允许继续观察。");
            Require(!platform.Probe(identity, false, new CloseWindowSource(childWindow, identity)).OnlySourceWindowVisible,
                "主进程身份不能冒充原窗口的实际子进程持有者。");
            Require(platform.Probe(identity, false) is { HasVisibleWindows: true, Reliable: true }, "仅结束根进程模式也必须保护子进程窗口。");
            Require(platform.Probe(identity, true) is { HasVisibleWindows: true, Reliable: true }, "进程树探测未保护子进程窗口。");
            Require(!platform.TryTerminate(identity, false, out _), "仅结束根进程模式未保护子进程的可见窗口。");
            Require(!platform.TryTerminate(identity, true, out _), "子进程有窗口时进程树保护失效。");
            CloseWindow(childWindow);
            WaitFor(() => platform.Probe(identity, true), s => s is { IsAlive: true, HasVisibleWindows: false, Reliable: true }, clock, "等待子进程窗口隐藏");
            Require(platform.TryTerminate(identity, true, out string terminateError), $"隐藏进程树结束失败：{terminateError}");
            Require(fixture.WaitForExit(5000) && child.WaitForExit(5000), "测试进程树未在期限内退出。");
            Record("PASS: hidden captured process tree terminated; visible child protected");
            Require(platform.Probe(identity, false).IsAlive == false, "已结束进程仍被报告为存活。");
            Record($"PASS: all native smoke checks ({clock.Elapsed.TotalSeconds:F2}s)");
            Record("CHECK: coordinator and real notification pipeline");
            MonitorSmokeTests.Run(fixtureExecutable, Record);
            Record("PASS: monitor discovery, notification persistence and automatic background cleanup");
            Record("CHECK: Steam-shaped main/helper process mapping");
            ApplicationTargetSmokeTests.Run(fixtureExecutable, Record);
            Record("PASS: verified main-process targeting and CEF window protection");
            return 0;
        }
        catch (Exception error)
        {
            Record("FAIL: " + error);
            return 1;
        }
        finally
        {
            // These Process objects refer only to fixture processes created by
            // this test (directly or through its private fixture command).
            foreach (Process fixture in fixtures)
            {
                try
                {
                    if (!fixture.HasExited)
                    {
                        fixture.Kill();
                        fixture.WaitForExit(2000);
                    }
                }
                catch (Exception cleanupError) when (cleanupError is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Record("Cleanup: " + cleanupError.Message);
                }

                fixture.Dispose();
            }

            File.WriteAllLines(LogPath, log);
            try
            {
                Directory.Delete(fixtureDirectory, true);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                File.AppendAllText(LogPath, "Cleanup directory: " + cleanupError.Message + Environment.NewLine);
            }
        }
    }

    private static nint FindWindow(WindowsPlatform platform, int pid) =>
        platform.ListWindows().FirstOrDefault(w => w.Process.Pid == pid && w.Title.StartsWith(TestFixtureForm.TitlePrefix, StringComparison.Ordinal))?.Handle ?? 0;

    internal static void CopyFixturePayload(string sourceDirectory, string appHost, string destination, string appHostName)
    {
        Directory.CreateDirectory(destination);
        // A portable EXE can be placed on the user's desktop. Never copy its
        // surrounding directory: only this application's known runtime payload
        // is needed by a renamed apphost or a self-contained bundle.
        foreach (string fileName in new[]
        {
            "CloseMeansExit.dll", "CloseMeansExit.Core.dll",
            "CloseMeansExit.deps.json", "CloseMeansExit.runtimeconfig.json"
        })
        {
            string source = Path.Combine(sourceDirectory, fileName);
            if (File.Exists(source)) File.Copy(source, Path.Combine(destination, fileName));
        }
        File.Copy(appHost, Path.Combine(destination, appHostName));
    }

    private static T WaitFor<T>(Func<T> sample, Func<T, bool> ready, Stopwatch clock, string step)
    {
        var local = Stopwatch.StartNew();
        do
        {
            T result = sample();
            if (ready(result))
            {
                return result;
            }

            if (clock.Elapsed > TimeSpan.FromSeconds(30) || local.Elapsed > TimeSpan.FromSeconds(8))
            {
                throw new TimeoutException(step + "超时。");
            }

            Thread.Sleep(50);
        }
        while (true);
    }

    private static void CloseWindow(nint hwnd)
    {
        // WinForms treats a bare external WM_CLOSE as TaskManagerClosing. Use
        // the user-close system command so the tray-like fixture cancels closing
        // and hides its window, just as it does for the title-bar close button.
        Command(hwnd, 0x0112, 0xF060); // WM_SYSCOMMAND / SC_CLOSE
    }

    private static nuint Command(nint hwnd, uint message, nuint wParam = 0)
    {
        Require(NativeMethods.SendMessageTimeout(hwnd, message, wParam, 0, NativeMethods.SmtoAbortIfHung, 3000, out nuint result) != 0,
            $"测试窗口消息 0x{message:X} 失败。");
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
