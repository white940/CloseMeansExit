using System.Diagnostics;
using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

/// <summary>
/// Uses only private copies of this test fixture to model Steam's main process
/// and CEF-owned windows. It never enumerates Steam by name or operates on a
/// real Steam installation.
/// </summary>
internal static class ApplicationTargetSmokeTests
{
    internal static void Run(string fixtureExecutable, Action<string> report)
    {
        string fixtureDirectory = Path.Combine(Path.GetTempPath(), "CloseMeansExit-steam-" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.GetDirectoryName(fixtureExecutable)!;
        var fixtures = new List<Process>();
        try
        {
            string mainExecutable = PrepareCopy(sourceDirectory, fixtureExecutable, fixtureDirectory, "Steam.exe");
            string helperDirectory = Path.Combine(fixtureDirectory, "bin", "cef", "cef.win64");
            string unrelatedDirectory = Path.Combine(fixtureDirectory, "other-install", "bin", "cef", "cef.win64");
            string helperExecutable = PrepareCopy(sourceDirectory, fixtureExecutable, helperDirectory, "steamwebhelper.exe");
            string unrelatedExecutable = PrepareCopy(sourceDirectory, fixtureExecutable, unrelatedDirectory, "steamwebhelper.exe");

            var platform = new WindowsPlatform();
            Process root = Process.Start(new ProcessStartInfo(mainExecutable, "--fixture")
            {
                UseShellExecute = false,
                WorkingDirectory = fixtureDirectory
            }) ?? throw new InvalidOperationException("Steam 形态主夹具启动失败。");
            fixtures.Add(root);
            _ = root.Handle;
            nint mainWindow = WaitFor(() => WindowForOwner(platform, root.Id), h => h != 0, "等待 Steam 形态主窗口");
            ProcessIdentity rootIdentity = platform.TryGetIdentity(root.Id)
                ?? throw new InvalidOperationException("无法确认 Steam 形态主夹具身份。");

            Process helper = CaptureChild(Command(mainWindow, TestFixtureForm.SpawnSteamWindowMessage), fixtures, helperExecutable);
            nint helperWindow = WaitFor(() => WindowForOwner(platform, helper.Id), h => h != 0, "等待 Steam 形态辅助窗口");
            ProcessIdentity helperIdentity = platform.TryGetIdentity(helper.Id)
                ?? throw new InvalidOperationException("无法确认 Steam 形态辅助夹具身份。");
            Require(RulePaths.Equal(helperIdentity.ExecutablePath, helperExecutable), "Steam 形态辅助进程不在预期路径。");
            ProcessIdentity resolved = platform.ResolveApplicationProcess(helperIdentity);
            Require(SameIdentity(resolved, rootIdentity), "辅助窗口没有解析到已验证的 Steam 主进程。");
            WindowEntry selected = platform.ListWindows().Single(w => w.Handle == helperWindow);
            Require(SameIdentity(selected.Process, rootIdentity), "当前窗口列表仍把辅助程序当成独立应用。");
            var rule = new AppRule { ExecutablePath = selected.Process.ExecutablePath };
            Require(RulePaths.Equal(rule.ExecutablePath, mainExecutable) &&
                !RulePaths.Equal(rule.ExecutablePath, helperExecutable), "从窗口选择的黑名单路径必须指向主程序。");
            Require(!platform.TryTerminate(rootIdentity, false, out _), "辅助进程有窗口时不得结束 Steam 形态主进程。");
            report("PASS: CEF-owned fixture window resolves to the verified Steam main process and rule path");

            ProcessIdentity forged = helperIdentity with { StartTimeUtcTicks = helperIdentity.StartTimeUtcTicks + 1 };
            Require(platform.ResolveApplicationProcess(forged) == forged,
                "辅助进程创建时间不匹配时不得沿父链扩大结束目标。");

            Process unrelated = CaptureChild(Command(mainWindow, TestFixtureForm.SpawnUnrelatedSteamWindowMessage), fixtures, unrelatedExecutable);
            nint unrelatedWindow = WaitFor(() => WindowForOwner(platform, unrelated.Id), h => h != 0, "等待不同安装路径的辅助窗口");
            ProcessIdentity unrelatedIdentity = platform.TryGetIdentity(unrelated.Id)
                ?? throw new InvalidOperationException("无法确认不同安装路径的辅助夹具身份。");
            Require(platform.ResolveApplicationProcess(unrelatedIdentity) == unrelatedIdentity,
                "同名 Steam 祖先不能接管不同安装目录的辅助程序。");
            Require(platform.ListWindows().Single(w => w.Handle == unrelatedWindow).Process == unrelatedIdentity,
                "不同安装目录的窗口不能被错误合并到 Steam 主进程。");
            unrelated.Kill();
            Require(unrelated.WaitForExit(3000), "不同安装目录测试夹具未退出。");
            Require(platform.ResolveApplicationProcess(unrelatedIdentity) == unrelatedIdentity,
                "已退出辅助进程不能通过仍存活的祖先被重新采纳。");
            report("PASS: mismatched installation, stale identity and exited helper do not broaden the target");

            Command(helperWindow, TestFixtureForm.CreateSecondaryMessage);
            WindowEntry[] helperWindows = WaitFor(() => WindowsForOwner(platform, helper.Id), w => w.Length >= 2,
                "等待 Steam 形态第二窗口");
            CloseWindow(helperWindow);
            Require(!root.HasExited && !helper.HasExited && platform.Probe(rootIdentity, false) is
                { IsAlive: true, IdentityMatches: true, HasVisibleWindows: true, Reliable: true },
                "同一辅助进程的第二窗口必须继续保护主程序。");
            Require(!platform.TryTerminate(rootIdentity, false, out _), "第二辅助窗口存在时误结束主进程。");
            foreach (WindowEntry window in helperWindows.Where(w => w.Handle != helperWindow)) CloseWindow(window.Handle);
            WaitFor(() => platform.Probe(rootIdentity, false),
                s => s is { IsAlive: true, IdentityMatches: true, HasVisibleWindows: false, Reliable: true },
                "等待 Steam 形态的全部窗口隐藏");

            Require(platform.TryTerminate(rootIdentity, false, out string error), "Steam 形态主程序结束失败：" + error);
            Require(root.WaitForExit(3000), "Steam 形态主程序未退出。");
            Require(!helper.HasExited, "仅结束主程序的规则不得改为只结束或同时结束辅助进程。");
            report("PASS: other CEF windows protect the main process; root-only execution ends the main fixture instance");
        }
        finally
        {
            // Objects are captured exclusively from Process.Start or the private
            // fixture's child-PID reply; no name-based cleanup is permitted.
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
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    report("Cleanup Steam-shaped fixture: " + error.Message);
                }
                finally { fixture.Dispose(); }
            }

            string fullDirectory = Path.GetFullPath(fixtureDirectory);
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "CloseMeansExit-steam-");
            if (fullDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullDirectory))
            {
                try { Directory.Delete(fullDirectory, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    report("Cleanup Steam-shaped directory: " + error.Message);
                }
            }
        }
    }

    private static string PrepareCopy(string sourceDirectory, string fixtureExecutable, string destination, string name)
    {
        SmokeTests.CopyFixturePayload(sourceDirectory, fixtureExecutable, destination, name);
        return Path.Combine(destination, name);
    }

    private static Process CaptureChild(nuint pid, List<Process> fixtures, string expectedPath)
    {
        Require(pid > 0 && pid <= int.MaxValue, "Steam 形态辅助夹具 PID 无效。");
        Process child = Process.GetProcessById((int)pid);
        try
        {
            _ = child.Handle;
            Require(child.MainModule is { } module && RulePaths.Equal(module.FileName, expectedPath),
                "测试子进程未指向专用临时夹具；不能采纳该 PID。");
            fixtures.Add(child);
            return child;
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private static WindowEntry[] WindowsForOwner(WindowsPlatform platform, int pid) =>
        platform.ListWindows().Where(w =>
        {
            NativeMethods.GetWindowThreadProcessId(w.Handle, out uint owner);
            return owner == (uint)pid;
        }).ToArray();

    private static nint WindowForOwner(WindowsPlatform platform, int pid) =>
        WindowsForOwner(platform, pid).FirstOrDefault(w => w.Title.StartsWith(TestFixtureForm.TitlePrefix, StringComparison.Ordinal))?.Handle ?? 0;

    private static bool SameIdentity(ProcessIdentity first, ProcessIdentity second) =>
        first.Pid == second.Pid && first.StartTimeUtcTicks == second.StartTimeUtcTicks &&
        RulePaths.Equal(first.ExecutablePath, second.ExecutablePath);

    private static T WaitFor<T>(Func<T> sample, Func<T, bool> ready, string step)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            T result = sample();
            if (ready(result)) return result;
            if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(step + "超时。");
            Thread.Sleep(30);
        } while (true);
    }

    private static void CloseWindow(nint window) => Command(window, 0x0112, 0xF060);

    private static nuint Command(nint window, uint message, nuint parameter = 0)
    {
        Require(NativeMethods.SendMessageTimeout(window, message, parameter, 0, NativeMethods.SmtoAbortIfHung, 3000, out nuint result) != 0,
            $"Steam 形态测试消息 0x{message:X} 失败。");
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
