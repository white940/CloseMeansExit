using System.Diagnostics;

namespace CloseMeansExit.Windows;

/// <summary>A deliberately tray-like app used only by the Windows smoke test.</summary>
internal sealed class TestFixtureForm : Form
{
    internal const uint CreateSecondaryMessage = 0x8000 + 0x51;
    internal const uint SaveDialogMessage = 0x8000 + 0x52;
    internal const uint MinimizeMessage = 0x8000 + 0x53;
    internal const uint RestoreMessage = 0x8000 + 0x54;
    internal const uint SpawnHelperMessage = 0x8000 + 0x55;
    internal const uint SpawnProtectedHelperMessage = 0x8000 + 0x56;
    internal const uint CleanupProtectedHelperMessage = 0x8000 + 0x57;
    internal const uint SpawnSteamWindowMessage = 0x8000 + 0x58;
    internal const uint SpawnUnrelatedSteamWindowMessage = 0x8000 + 0x59;
    internal const uint CleanupHelpersMessage = 0x8000 + 0x5A;
    internal const string TitlePrefix = "CloseMeansExit Test Fixture ";
    internal const string SaveTitle = "CloseMeansExit Fixture Save Confirmation";
    private bool _allowExit;
    private readonly List<TestFixtureForm> _children = [];
    private readonly List<Process> _helperProcesses = [];
    private readonly List<Process> _protectedHelpers = [];
    private readonly NotifyIcon _tray;

    internal TestFixtureForm(bool secondary)
    {
        Text = TitlePrefix + Environment.ProcessId + (secondary ? " / Secondary" : "");
        Width = 540;
        Height = 270;
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(20),
            AutoScroll = true
        };
        layout.Controls.Add(new Label
        {
            Text = "测试专用：点叉仅隐藏窗口，进程继续运行。",
            AutoSize = true,
            Margin = new Padding(3, 3, 3, 15)
        });
        AddButton(layout, "隐藏到托盘（测试）", Hide);
        AddButton(layout, "创建第二个窗口", CreateSecondary);
        AddButton(layout, "显示保存提示", ShowSaveDialog);
        AddButton(layout, "完全退出", ExitFixture);
        Controls.Add(layout);
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示测试窗口", null, (_, _) => RestoreFixture());
        menu.Items.Add("完全退出测试程序", null, (_, _) => ExitFixture());
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CloseMeansExit 测试程序",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => RestoreFixture();
        FormClosing += (_, e) =>
        {
            if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CleanupProtectedHelpers();
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message message)
    {
        switch (unchecked((uint)message.Msg))
        {
            case CreateSecondaryMessage:
                CreateSecondary();
                message.Result = 1;
                return;
            case SaveDialogMessage:
                Hide();
                ShowSaveDialog();
                message.Result = 1;
                return;
            case MinimizeMessage:
                WindowState = FormWindowState.Minimized;
                message.Result = 1;
                return;
            case RestoreMessage:
                Show();
                WindowState = FormWindowState.Normal;
                message.Result = 1;
                return;
            case SpawnHelperMessage:
                message.Result = SpawnHelper();
                return;
            case SpawnProtectedHelperMessage:
                message.Result = SpawnProtectedHelper();
                return;
            case CleanupProtectedHelperMessage:
                CleanupProtectedHelpers();
                message.Result = 1;
                return;
            case SpawnSteamWindowMessage:
                message.Result = SpawnSteamWindow(false);
                return;
            case SpawnUnrelatedSteamWindowMessage:
                message.Result = SpawnSteamWindow(true);
                return;
            case CleanupHelpersMessage:
                message.Result = CleanupHelpers() ? 1 : 0;
                return;
        }

        base.WndProc(ref message);
    }

    private static void AddButton(Control parent, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(220, 28) };
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
    }

    private void CreateSecondary()
    {
        var child = new TestFixtureForm(true);
        _children.Add(child);
        // Keep this as an independent top-level window. Hiding/minimizing an
        // owner can automatically hide its owned windows on Windows.
        child.Show();
    }

    private void ShowSaveDialog() => MessageBox.Show(this,
        "此对话框用于检查：存在保存提示时不能结束进程。", SaveTitle,
        MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

    private void RestoreFixture()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private int SpawnHelper()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("缺少测试程序路径。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add("--fixture-secondary");
        Process helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动测试子进程。");
        _helperProcesses.Add(helper);
        return helper.Id;
    }

    private int SpawnProtectedHelper()
    {
        // Keep an actual Windows-directory child alive without showing a console
        // or starting any background work. Closing its private stdin ends it.
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p CloseMeansExitFixtureWait=");
        Process helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动测试系统子进程。");
        _ = helper.Handle;
        _protectedHelpers.Add(helper);
        return helper.Id;
    }

    private int SpawnSteamWindow(bool unrelatedInstallation)
    {
        string baseDirectory = ApplicationTargetSmokeTests.ExpandFixturePath(AppContext.BaseDirectory);
        string fixturePrefix = Path.Combine(ApplicationTargetSmokeTests.ExpandFixturePath(Path.GetTempPath()), "CloseMeansExit-steam-");
        if (!baseDirectory.StartsWith(fixturePrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Environment.ProcessPath), "Steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Steam 形态测试只允许从专用临时夹具目录启动。");

        string installation = unrelatedInstallation ? Path.Combine(baseDirectory, "other-install") : baseDirectory;
        string executable = Path.Combine(installation, "bin", "cef", "cef.win64", "steamwebhelper.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add("--fixture-secondary");
        Process helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Steam 形态窗口夹具。");
        _ = helper.Handle;
        _helperProcesses.Add(helper);
        Hide();
        return helper.Id;
    }

    private void CleanupProtectedHelpers()
    {
        foreach (Process helper in _protectedHelpers)
        {
            try
            {
                if (!helper.HasExited)
                {
                    helper.StandardInput.Close();
                    if (!helper.WaitForExit(2000))
                    {
                        helper.Kill();
                        helper.WaitForExit(2000);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // A test helper that already exited needs no further cleanup.
            }
            finally
            {
                helper.Dispose();
            }
        }

        _protectedHelpers.Clear();
    }

    private void ExitFixture()
    {
        _allowExit = true;
        CleanupProtectedHelpers();
        foreach (TestFixtureForm child in _children)
        {
            child._allowExit = true;
            child.Close();
        }

        CleanupHelpers();

        Close();
        Application.ExitThread();
    }

    private bool CleanupHelpers()
    {
        bool cleaned = true;
        foreach (Process helper in _helperProcesses.ToArray())
        {
            try
            {
                if (!helper.HasExited)
                {
                    helper.Kill();
                    if (!helper.WaitForExit(2000))
                    {
                        cleaned = false;
                        continue;
                    }
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                cleaned = false;
                continue;
            }

            helper.Dispose();
            _helperProcesses.Remove(helper);
        }

        return cleaned;
    }
}
