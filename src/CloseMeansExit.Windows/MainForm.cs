using System.ComponentModel;
using System.Diagnostics;
using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;
using Microsoft.Win32;

namespace CloseMeansExit.Windows;

internal sealed class MainForm : Form
{
    private readonly string _dataDirectory;
    private readonly string _settingsPath;
    private readonly WindowsPlatform _platform = new();
    private readonly MonitorCoordinator _monitor;
    private readonly ResidencyNotifications _notifications;
    private readonly NotifyIcon _tray;
    private readonly BindingList<DiscoveryRow> _discoveries = [];
    private readonly BindingList<RuleRow> _ruleRows = [];
    private readonly BindingList<WindowRow> _windows = [];
    private readonly DataGridView _discoveryGrid = Grid();
    private readonly DataGridView _ruleGrid = Grid();
    private readonly DataGridView _windowGrid = Grid();
    private readonly TextBox _logs = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Label _state = new() { AutoSize = true, ForeColor = Color.FromArgb(28, 107, 76), Padding = new Padding(0, 10, 12, 0) };
    private readonly Button _pause = new() { Text = "暂停监测", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "登录 Windows 后启动", AutoSize = true, Padding = new Padding(12, 9, 0, 0) };
    private AppSettings _settings;
    private bool _exiting;
    private bool _loading = true;
    private bool _shown;
    private bool _monitorAvailable;
    private bool _hideNotice;
    private readonly bool _startInTray;

    public MainForm(string dataDirectory, bool startInTray)
    {
        _dataDirectory = dataDirectory;
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _settings = SettingsStore.Load(_settingsPath, out string? warning);
        _startInTray = startInTray;
        Text = $"叉掉就退 · CloseMeansExit {Application.ProductVersion}";
        Size = new Size(1060, 690);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;

        _monitor = new MonitorCoordinator(_platform);
        // Ignore protected targets even when an externally edited configuration contains them.
        _settings.Rules.RemoveAll(r => IsProtected(r.ExecutablePath));
        _monitor.SetRules(_settings.Rules);
        _notifications = new ResidencyNotifications(_platform,
            path => _settings.Rules.FirstOrDefault(r => RulePaths.Equal(r.ExecutablePath, path)),
            () => _monitor.Paused, AddRuleFromNotification, AppendLog);
        _monitor.Discovered += record => OnUi(() => ShowDiscovery(record));
        _monitor.Log += message => OnUi(() => AppendLog(message));

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => Reveal());
        menu.Items.Add("暂停 / 恢复监测", null, (_, _) => TogglePause());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出工具", null, (_, _) => ExitTool());
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "叉掉就退 · 正在观察", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Reveal();

        var header = new Panel { Dock = DockStyle.Top, Height = 100, Padding = new Padding(22, 15, 22, 10), BackColor = Color.FromArgb(242, 247, 244) };
        header.Controls.Add(new Label { Text = "叉掉就退", Font = new Font(Font.FontFamily, 19, FontStyle.Bold), AutoSize = true, Location = new Point(22, 12) });
        header.Controls.Add(new Label { Text = "关掉窗口，就让应用退场。由你决定哪些程序必须退出。", AutoSize = true, Location = new Point(24, 58), ForeColor = Color.FromArgb(70, 80, 74) });

        var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 53, Padding = new Padding(16, 7, 10, 5), WrapContents = false };
        controls.Controls.AddRange([_state, _pause, _startup, Button("打开配置与日志", (_, _) => OpenDataDirectory()), Button("退出工具", (_, _) => ExitTool())]);
        _pause.Click += (_, _) => TogglePause();
        _startup.Checked = StartupRegistration.IsEnabled();
        _startup.CheckedChanged += (_, _) => ChangeStartup();

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 9) };
        ConfigureDiscoveryGrid();
        ConfigureRuleGrid();
        ConfigureWindowGrid();
        tabs.TabPages.Add(Page("发现记录", "记录窗口消失后仍在运行的应用。加入黑名单后，从下一次关闭操作开始生效。", _discoveryGrid,
            Button("将所选应用加入黑名单", (_, _) => EditRule(SelectedDiscoveryPath())),
            Button("清空记录", (_, _) => _discoveries.Clear())));
        tabs.TabPages.Add(Page("黑名单", "仅对启用的路径规则执行。强制结束可能丢失未保存内容、下载或同步任务。", _ruleGrid,
            Button("选择 EXE 添加", (_, _) => BrowseRule()),
            Button("编辑所选规则", (_, _) => EditRule(SelectedRulePath())),
            Button("移除所选规则", (_, _) => RemoveRule())));
        tabs.TabPages.Add(Page("当前窗口", "选择当前运行的应用添加规则。最小化窗口仍视为存在，不会触发退出。", _windowGrid,
            Button("刷新窗口", async (_, _) => await RefreshWindowsAsync()),
            Button("将所选应用加入黑名单", (_, _) => EditRule(SelectedWindowPath()))));
        tabs.TabPages.Add(Page("运行日志", "日志保存在本机。不会上传应用列表、窗口标题或任何遥测。", _logs,
            Button("打开日志目录", (_, _) => OpenDataDirectory())));
        Controls.Add(tabs);
        Controls.Add(header);
        Controls.Add(controls);
        RefreshRules();
        UpdateState();
        _loading = false;
        if (warning is not null) AppendLog(warning);
        Shown += async (_, _) =>
        {
            _shown = true;
            if (_startInTray) Hide();
            try { _monitor.Start(); _monitorAvailable = true; }
            catch (Exception e)
            {
                _monitor.SetPaused(true);
                UpdateState();
                _state.Text = "监测未启动";
                _pause.Enabled = false;
                _tray.Text = "叉掉就退 · 监测未启动";
                AppendLog($"输入监测未能启动：{e.Message}");
                MessageBox.Show(this, $"无法启动关闭按钮监测：{e.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            await RefreshWindowsAsync();
        };
    }

    private void ConfigureDiscoveryGrid()
    {
        _discoveryGrid.AutoGenerateColumns = false;
        Column(_discoveryGrid, "Time", "发现时间", 115);
        Column(_discoveryGrid, "Application", "应用", 150);
        Column(_discoveryGrid, "Status", "检测结果", 240);
        Column(_discoveryGrid, "Memory", "工作集", 95);
        Column(_discoveryGrid, "Path", "程序路径", 280, true);
        _discoveryGrid.DataSource = _discoveries;
        _discoveryGrid.CellDoubleClick += (_, _) => EditRule(SelectedDiscoveryPath());
    }

    private void ConfigureRuleGrid()
    {
        _ruleGrid.AutoGenerateColumns = false;
        Column(_ruleGrid, "Application", "应用", 160);
        Column(_ruleGrid, "State", "状态", 80);
        Column(_ruleGrid, "Scope", "结束范围", 110);
        Column(_ruleGrid, "Custom", "自绘兼容", 100);
        Column(_ruleGrid, "Path", "程序路径", 380, true);
        _ruleGrid.DataSource = _ruleRows;
        _ruleGrid.CellDoubleClick += (_, _) => EditRule(SelectedRulePath());
    }

    private void ConfigureWindowGrid()
    {
        _windowGrid.AutoGenerateColumns = false;
        Column(_windowGrid, "Application", "应用", 160);
        Column(_windowGrid, "Title", "窗口标题", 260);
        Column(_windowGrid, "Memory", "工作集", 95);
        Column(_windowGrid, "Path", "程序路径", 300, true);
        _windowGrid.DataSource = _windows;
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        RowTemplate = { Height = 29 }, AllowUserToResizeRows = false
    };

    private static void Column(DataGridView grid, string property, string header, int width, bool fill = false) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = header, Width = width,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, MinimumWidth = 60 });

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(5, 3, 5, 3), Margin = new Padding(3, 0, 10, 0) };
        button.Click += handler;
        return button;
    }

    private static TabPage Page(string title, string hint, Control content, params Button[] buttons)
    {
        var page = new TabPage(title) { Padding = new Padding(14) };
        var note = new Label { Text = hint, Dock = DockStyle.Top, Height = 43, ForeColor = Color.FromArgb(75, 80, 85) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 10, 0, 0) };
        bar.Controls.AddRange(buttons);
        page.Controls.Add(content);
        page.Controls.Add(note);
        page.Controls.Add(bar);
        return page;
    }

    private void ShowDiscovery(DiscoveryRecord record)
    {
        string path = record.Observation.Process.ExecutablePath;
        var existing = _discoveries.FirstOrDefault(r => RulePaths.Equal(r.Path, path));
        if (existing is not null) _discoveries.Remove(existing);
        _discoveries.Insert(0, new DiscoveryRow(record));
        if (_discoveries.Count > 200) _discoveries.RemoveAt(_discoveries.Count - 1);
        _notifications.Enqueue(record);
    }

    private string? SelectedDiscoveryPath() => (_discoveryGrid.CurrentRow?.DataBoundItem as DiscoveryRow)?.Path;
    private string? SelectedRulePath() => (_ruleGrid.CurrentRow?.DataBoundItem as RuleRow)?.Path;
    private string? SelectedWindowPath() => (_windowGrid.CurrentRow?.DataBoundItem as WindowRow)?.Path;

    private bool IsProtected(string path) => RulePaths.IsProtectedPath(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.ProcessPath ?? "");

    private void EditRule(string? path)
    {
        if (path is null)
        {
            MessageBox.Show(this, "请先选择一个应用。", Text);
            return;
        }
        if (IsProtected(path))
        {
            MessageBox.Show(this, "不能将 Windows 系统目录中的程序或本工具加入黑名单。", Text);
            return;
        }
        var old = _settings.Rules.FirstOrDefault(r => RulePaths.Equal(r.ExecutablePath, path));
        using var dialog = new RuleDialog(old ?? new AppRule { ExecutablePath = RulePaths.Normalize(path) });
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var replacement = _settings.Rules.Where(r => !RulePaths.Equal(r.ExecutablePath, path)).ToList();
        replacement.Add(dialog.Rule);
        SaveRules(replacement);
    }

    private void BrowseRule()
    {
        using var picker = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", Title = "选择加入黑名单的应用主程序", CheckFileExists = true };
        if (picker.ShowDialog(this) == DialogResult.OK) EditRule(picker.FileName);
    }

    private void RemoveRule()
    {
        string? path = SelectedRulePath();
        if (path is not null) SaveRules(_settings.Rules.Where(r => !RulePaths.Equal(r.ExecutablePath, path)).ToList());
    }

    private void SaveRules(List<AppRule> rules)
    {
        if (!TrySaveRules(rules, out string error))
            MessageBox.Show(this, $"无法保存规则：{error}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        else _notifications.RulesChanged();
    }

    private bool TrySaveRules(List<AppRule> rules, out string error)
    {
        var next = new AppSettings { Rules = rules, StartWithWindows = _settings.StartWithWindows };
        try
        {
            SettingsStore.Save(_settingsPath, next);
            _settings = next;
            _monitor.SetRules(rules);
            RefreshRules();
            AppendLog("黑名单已保存；规则从下一次关闭操作开始生效。");
            error = "";
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    private NotificationSaveResult AddRuleFromNotification(string path, bool useCustomCloseZone, bool includeChildren)
    {
        if (_exiting || _monitor.Paused) return new(false, "监测已暂停，未更改黑名单。");
        if (IsProtected(path)) return new(false, "不能将系统程序或工具自身加入黑名单。");
        var old = _settings.Rules.FirstOrDefault(r => RulePaths.Equal(r.ExecutablePath, path));
        if (old?.Enabled == true && (!useCustomCloseZone || old.UseCustomCloseZone) &&
            old.IncludeChildren == includeChildren)
            return new(true, "该应用已在黑名单中。");
        var rule = (old ?? new AppRule { ExecutablePath = RulePaths.Normalize(path) }) with
        {
            Enabled = true,
            IncludeChildren = includeChildren,
            UseCustomCloseZone = old?.UseCustomCloseZone == true || useCustomCloseZone
        };
        var rules = _settings.Rules.Where(r => !RulePaths.Equal(r.ExecutablePath, path)).Append(rule).ToList();
        return TrySaveRules(rules, out string error)
            ? new(true, "已加入黑名单；下一次关闭开始生效。") : new(false, error);
    }

    private void RefreshRules()
    {
        _ruleRows.Clear();
        foreach (var rule in _settings.Rules.OrderBy(r => r.ExecutablePath, StringComparer.OrdinalIgnoreCase)) _ruleRows.Add(new RuleRow(rule));
    }

    private async Task RefreshWindowsAsync()
    {
        try
        {
            var entries = await Task.Run(_platform.ListWindows);
            if (IsDisposed) return;
            _windows.Clear();
            foreach (var entry in entries.Where(e => !IsProtected(e.Process.ExecutablePath)).OrderBy(e => e.Process.ExecutablePath))
                _windows.Add(new WindowRow(entry));
        }
        catch (Exception e) { AppendLog($"无法刷新窗口：{e.Message}"); }
    }

    private void TogglePause()
    {
        if (!_monitorAvailable)
        {
            MessageBox.Show(this, "关闭按钮监测尚未启动，请退出工具后重新运行。", Text);
            return;
        }
        _monitor.SetPaused(!_monitor.Paused);
        _notifications.Reset();
        UpdateState();
    }

    private void UpdateState()
    {
        _state.Text = _monitor.Paused ? "监测已暂停" : "正在监测";
        _pause.Text = _monitor.Paused ? "恢复监测" : "暂停监测";
        if (_tray is not null) _tray.Text = _monitor.Paused ? "叉掉就退 · 已暂停" : "叉掉就退 · 正在监测";
    }

    private void ChangeStartup()
    {
        if (_loading) return;
        bool previous = StartupRegistration.IsEnabled();
        try
        {
            StartupRegistration.SetEnabled(_startup.Checked);
            var next = new AppSettings { Rules = _settings.Rules, StartWithWindows = _startup.Checked };
            SettingsStore.Save(_settingsPath, next);
            _settings = next;
        }
        catch (Exception e)
        {
            string message = e.Message;
            try { StartupRegistration.SetEnabled(previous); }
            catch (Exception rollbackError) { message += $"\n恢复原启动项失败：{rollbackError.Message}"; }
            _loading = true;
            _startup.Checked = StartupRegistration.IsEnabled();
            _loading = false;
            MessageBox.Show(this, $"无法完成开机启动设置：{message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AppendLog(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        if (_logs.TextLength > 100_000) _logs.Clear();
        _logs.AppendText(line);
        try
        {
            string path = Path.Combine(_dataDirectory, "activity.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_048_576) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed && !Disposing) action(); }); }
        catch (InvalidOperationException) { }
    }

    private void OpenDataDirectory() => Process.Start(new ProcessStartInfo(_dataDirectory) { UseShellExecute = true });
    private void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ExitTool() { _exiting = true; _notifications.Reset(); Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_hideNotice)
            {
                _tray.ShowBalloonTip(4000, "叉掉就退仍在监测", "主窗口已收起到通知区域。要完全退出，请右键托盘图标选择“退出工具”。", ToolTipIcon.Info);
                _hideNotice = true;
            }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _notifications.Dispose();
        if (_shown) _monitor.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    private sealed class DiscoveryRow(DiscoveryRecord record)
    {
        public string Time => record.Observation.At.ToLocalTime().ToString("MM-dd HH:mm:ss");
        public string Application => System.IO.Path.GetFileName(Path);
        public string Path => record.Observation.Process.ExecutablePath;
        public string Status => record.Status;
        public string Memory => $"{record.WorkingSetBytes / 1048576.0:F1} MB";
    }
    private sealed class RuleRow(AppRule rule)
    {
        public string Application => System.IO.Path.GetFileName(Path);
        public string Path => rule.ExecutablePath;
        public string State => rule.Enabled ? "启用" : "停用";
        public string Scope => rule.IncludeChildren ? "进程及子进程" : "仅主进程";
        public string Custom => rule.UseCustomCloseZone ? "已开启" : "关闭";
    }
    private sealed class WindowRow(WindowEntry entry)
    {
        public string Application => System.IO.Path.GetFileName(Path);
        public string Path => entry.Process.ExecutablePath;
        public string Title => entry.Title;
        public string Memory => $"{entry.WorkingSetBytes / 1048576.0:F1} MB";
    }
}

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "CloseMeansExit";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        string? value = key?.GetValue(Name) as string;
        return value is not null && value == Command();
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "CloseMeansExit.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请在打包后的 CloseMeansExit.exe 中启用开机启动。");
            key.SetValue(Name, Command());
        }
        else key.DeleteValue(Name, false);
    }
    private static string Command() => $"\"{Environment.ProcessPath}\" --tray";
}
