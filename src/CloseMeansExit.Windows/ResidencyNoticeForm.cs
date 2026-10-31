using CloseMeansExit.Core;

namespace CloseMeansExit.Windows;

/// <summary>
/// A persistent, non-activating notification. The owner saves a requested rule
/// and closes this form only after that save succeeds.
/// </summary>
internal sealed class ResidencyNoticeForm : Form
{
    private readonly Panel _body;
    private readonly TableLayoutPanel _content;
    private readonly FlowLayoutPanel _actions;
    private readonly Label _application;
    private readonly Label _description;
    private readonly Label _memory;
    private readonly Label _path;
    private readonly Label _risk;
    private readonly Label _pending;
    private readonly Label _error;
    private readonly CheckBox _customCloseZone;
    private readonly CheckBox _includeChildren;
    private readonly bool _allowCustomCloseZone;
    private readonly bool _enableCloseCompatibility;
    private readonly Button _add;
    private readonly Button _dismiss;
    private readonly ToolTip _toolTip = new();
    private readonly Font _applicationFont;
    private readonly Font _memoryFont;
    private readonly Font _bodyFont;
    private bool _busy;
    private bool _addInFlight;
    private bool _dismissRaised;
    private bool _reflowing;
    private bool _shown;
    private bool _resourcesDisposed;

    public event Action? AddRequested;
    public event Action? DismissRequested;
    public bool UseCustomCloseZone => _enableCloseCompatibility || (_allowCustomCloseZone && _customCloseZone.Checked);
    public bool IncludeChildren => _includeChildren.Checked;

    public ResidencyNoticeForm(CloseObservation observation, long workingSetBytes, bool reenableExistingRule,
        bool enableCloseCompatibility = false, bool existingIncludeChildren = false)
    {
        _enableCloseCompatibility = enableCloseCompatibility;
        Text = "叉掉就退 · 后台驻留提示";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        _bodyFont = new Font("Microsoft YaHei UI", 9F);
        Font = _bodyFont;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(36, 47, 42);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(430, 340);

        string executablePath = observation.Process.ExecutablePath;
        string applicationName = Path.GetFileName(executablePath);
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            applicationName = executablePath;
        }

        _applicationFont = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        _memoryFont = new Font(Font.FontFamily, 19F, FontStyle.Bold);
        _application = TextLabel(applicationName, 10);
        _application.Font = _applicationFont;
        string question = reenableExistingRule ? "要恢复启用它的黑名单规则吗？" : "要将这个应用加入黑名单吗？";
        _description = TextLabel(
            enableCloseCompatibility
                ? "这个应用已在黑名单中，但本次只确认窗口消失，未识别到关闭动作。要启用右上角关闭按钮兼容吗？"
                : observation.Evidence == CloseEvidence.WindowDisappeared
                ? "窗口已消失，进程仍在运行。" + question
                : "检测到关闭窗口后，应用仍驻留后台。" + question, 12);
        _memory = TextLabel(workingSetBytes > 0
            ? $"进程工作集  {workingSetBytes / (1024d * 1024d):F1} MB"
            : "进程工作集  暂不可用", 10);
        _memory.Font = _memoryFont;
        _memory.ForeColor = Color.FromArgb(27, 105, 74);
        _path = new Label
        {
            Text = executablePath,
            AutoEllipsis = true,
            AutoSize = false,
            Height = 24,
            Dock = DockStyle.Top,
            ForeColor = Color.FromArgb(99, 107, 103),
            Margin = new Padding(0, 0, 0, 14),
            UseMnemonic = false,
            AccessibleName = "应用完整路径"
        };
        _allowCustomCloseZone = observation.Evidence == CloseEvidence.WindowDisappeared;
        _customCloseZone = new CheckBox
        {
            Text = "兼容自绘关闭按钮（高级）",
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Checked = false,
            Visible = _allowCustomCloseZone && !enableCloseCompatibility,
            Margin = new Padding(0, 0, 0, 12),
            UseMnemonic = false
        };
        _includeChildren = new CheckBox
        {
            Text = "同时结束子进程（包括 Steam 启动的游戏）",
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Visible = string.Equals(Path.GetFileName(executablePath), "Steam.exe", StringComparison.OrdinalIgnoreCase),
            Checked = existingIncludeChildren,
            Margin = new Padding(0, 0, 0, 12),
            UseMnemonic = false
        };
        _risk = TextLabel(enableCloseCompatibility
            ? "仅对以后的关闭操作生效。兼容按右上角关闭区域判断，请先核对按钮位置；强制结束可能中断下载或同步。"
            : observation.Evidence == CloseEvidence.WindowDisappeared
                ? "本次尚未识别到关闭按钮。要用右上角叉号自动退出，请勾选关闭按钮兼容；也可使用 Alt+F4。强制结束可能中断下载或同步。"
                : "加入后，识别到下一次关闭操作时自动结束；可能中断下载或同步。", 0);
        _risk.ForeColor = Color.FromArgb(99, 107, 103);
        _pending = TextLabel("", 0);
        _pending.ForeColor = Color.FromArgb(99, 107, 103);
        _pending.Margin = new Padding(0, 12, 0, 0);
        _pending.Visible = false;
        _error = TextLabel("", 0);
        _error.ForeColor = Color.FromArgb(163, 44, 38);
        _error.Margin = new Padding(0, 12, 0, 0);
        _error.Visible = false;
        _error.AccessibleName = "规则保存错误";

        _toolTip.SetToolTip(_application, executablePath);
        _toolTip.SetToolTip(_path, executablePath);
        _toolTip.SetToolTip(_memory, "检测时的进程工作集；不代表结束应用后精确释放的物理内存。");
        _toolTip.SetToolTip(_customCloseZone,
            "开启后，将窗口右上角 48×40 逻辑像素视为关闭区域。请先核对应用布局，避免最小化、设置等按钮落在该区域。");

        _content = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 9,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _content.Controls.Add(_application, 0, 0);
        _content.Controls.Add(_description, 0, 1);
        _content.Controls.Add(_memory, 0, 2);
        _content.Controls.Add(_path, 0, 3);
        _content.Controls.Add(_customCloseZone, 0, 4);
        _content.Controls.Add(_includeChildren, 0, 5);
        _content.Controls.Add(_risk, 0, 6);
        _content.Controls.Add(_pending, 0, 7);
        _content.Controls.Add(_error, 0, 8);
        _body = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(20, 18, 20, 8)
        };
        _body.Controls.Add(_content);

        _add = new Button
        {
            Text = enableCloseCompatibility ? "启用关闭按钮兼容" : reenableExistingRule ? "恢复启用" : "加入黑名单",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(13, 5, 13, 5),
            Margin = new Padding(8, 0, 0, 0),
            BackColor = Color.FromArgb(29, 108, 77),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            AccessibleDescription = "保存规则；从下一次关闭操作开始生效。"
        };
        _add.FlatAppearance.BorderSize = 0;
        _dismiss = new Button
        {
            Text = "暂不处理",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(11, 5, 11, 5),
            Margin = new Padding(0, 0, 0, 0)
        };
        _actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20, 12, 20, 18),
            BackColor = Color.FromArgb(245, 248, 246)
        };
        _actions.Controls.Add(_add);
        _actions.Controls.Add(_dismiss);
        Controls.Add(_body);
        Controls.Add(_actions);
        AcceptButton = _add;
        CancelButton = _dismiss;
        _add.Click += (_, _) => RequestAdd();
        _dismiss.Click += (_, _) => RequestDismiss();
    }

    // This affects initial showing only. A deliberate click may still activate
    // the form and use its buttons through the keyboard as usual.
    protected override bool ShowWithoutActivation => true;

    public void SetBusy(bool busy)
    {
        if (IsDisposed)
        {
            return;
        }

        _busy = busy;
        _add.Enabled = !busy;
        _dismiss.Enabled = !busy;
        _customCloseZone.Enabled = !busy;
        _includeChildren.Enabled = !busy;
        ControlBox = !busy;
        UseWaitCursor = busy;
    }

    public void ShowSaveFailure(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        _addInFlight = false;
        SetBusy(false);
        string detail = string.IsNullOrWhiteSpace(message) ? "请稍后重试。" : message.Trim();
        _error.Text = "保存失败：" + (detail.Length > 1000 ? detail[..1000] + "…" : detail);
        _toolTip.SetToolTip(_error, detail);
        _error.Visible = true;
        if (_shown)
        {
            FitToWorkingArea(Screen.FromRectangle(Bounds));
        }
    }

    public void SetPendingCount(int count)
    {
        if (IsDisposed)
        {
            return;
        }

        count = Math.Max(0, count);
        _pending.Text = count > 0 ? $"另有 {count} 个应用等待确认" : "";
        _pending.Visible = count > 0;
        if (_shown)
        {
            FitToWorkingArea(Screen.FromRectangle(Bounds));
        }
    }

    public Rectangle PreferredBounds(Screen screen, int bottomOffset = 0)
    {
        Rectangle area = screen.WorkingArea;
        int margin = Math.Max(16, Scaled(16));
        int width = Math.Min(Width, Math.Max(1, area.Width - 2 * margin));
        int height = Math.Min(Height, Math.Max(1, area.Height - 2 * margin));
        int offset = Math.Clamp(bottomOffset, 0, Math.Max(0, area.Height - 2 * margin - height));
        return new Rectangle(area.Right - margin - width,
            Math.Max(area.Top + margin, area.Bottom - margin - offset - height), width, height);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shown = true;
        FitToWorkingArea(Screen.PrimaryScreen ?? Screen.FromControl(this));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_shown)
        {
            FitToWorkingArea(Screen.FromRectangle(Bounds));
        }
    }

    protected override void WndProc(ref Message message)
    {
        // Prevent user Alt+F4 while a save is pending. Programmatic Close()
        // still works, so the owner can close after a successful save.
        if (_busy && message.Msg == 0x0112 && (message.WParam.ToInt64() & 0xFFF0) == 0xF060)
        {
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel && !_addInFlight)
        {
            RaiseDismissOnce();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
        if (disposing && !_resourcesDisposed)
        {
            _applicationFont.Dispose();
            _memoryFont.Dispose();
            _bodyFont.Dispose();
            _resourcesDisposed = true;
        }
    }

    private void RequestAdd()
    {
        if (_busy)
        {
            return;
        }

        _error.Visible = false;
        _addInFlight = true;
        SetBusy(true);
        try
        {
            AddRequested?.Invoke();
        }
        catch (Exception error)
        {
            ShowSaveFailure(error.Message);
        }
    }

    private void RequestDismiss()
    {
        if (_busy)
        {
            return;
        }

        RaiseDismissOnce();
        if (!IsDisposed)
        {
            Close();
        }
    }

    private void RaiseDismissOnce()
    {
        if (_dismissRaised)
        {
            return;
        }

        _dismissRaised = true;
        DismissRequested?.Invoke();
    }

    private void FitToWorkingArea(Screen screen)
    {
        if (_reflowing || IsDisposed)
        {
            return;
        }

        _reflowing = true;
        try
        {
            int margin = Math.Max(16, Scaled(16));
            int maxWidth = Math.Max(1, screen.WorkingArea.Width - 2 * margin);
            int maxHeight = Math.Max(1, screen.WorkingArea.Height - 2 * margin);
            int nonClientWidth = Width - ClientSize.Width;
            int nonClientHeight = Height - ClientSize.Height;
            int windowWidth = Math.Min(Scaled(448), maxWidth);
            int clientWidth = Math.Max(1, windowWidth - nonClientWidth);
            _body.Padding = new Padding(Scaled(20), Scaled(18), Scaled(20), Scaled(8));
            _actions.Padding = new Padding(Scaled(20), Scaled(12), Scaled(20), Scaled(18));
            int contentWidth = Math.Max(1, clientWidth - _body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            _content.MinimumSize = new Size(contentWidth, 0);
            _content.MaximumSize = new Size(contentWidth, 0);
            _content.Width = contentWidth;
            foreach (Label label in new[] { _application, _description, _memory, _risk, _pending, _error })
            {
                label.MaximumSize = new Size(contentWidth, 0);
            }
            _customCloseZone.MaximumSize = new Size(contentWidth, 0);
            _includeChildren.MaximumSize = new Size(contentWidth, 0);

            _path.Height = Scaled(24);
            _content.PerformLayout();
            _actions.Width = clientWidth;
            _actions.PerformLayout();
            int desiredClientHeight = _content.GetPreferredSize(new Size(contentWidth, 0)).Height +
                _body.Padding.Vertical + _actions.GetPreferredSize(new Size(clientWidth, 0)).Height;
            Size = new Size(windowWidth, Math.Min(maxHeight, desiredClientHeight + nonClientHeight));
            Bounds = PreferredBounds(screen);
        }
        finally
        {
            _reflowing = false;
        }
    }

    private int Scaled(int value) => (int)Math.Ceiling(value * DeviceDpi / 96d);

    private static Label TextLabel(string text, int bottomMargin) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 0, 0, bottomMargin),
        UseMnemonic = false
    };
}
