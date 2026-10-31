using CloseMeansExit.Core;

namespace CloseMeansExit.Windows;

internal sealed class RuleDialog : Form
{
    private readonly AppRule _original;
    private readonly CheckBox _enabled = new() { Text = "启用规则：下次点关闭后，结束仍驻留的应用", AutoSize = true };
    private readonly CheckBox _children = new() { Text = "同时结束它的子进程（高级）", AutoSize = true };
    private readonly CheckBox _custom = new() { Text = "启用自绘标题栏右上角兼容检测（高级）", AutoSize = true };
    public AppRule Rule => _original with { Enabled = _enabled.Checked, IncludeChildren = _children.Checked, UseCustomCloseZone = _custom.Checked };

    public RuleDialog(AppRule rule)
    {
        _original = rule;
        Text = "黑名单规则";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(560, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        stack.Controls.Add(new Label { Text = Path.GetFileName(rule.ExecutablePath), Font = new Font(Font.FontFamily, 13, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
        stack.Controls.Add(new TextBox { Text = rule.ExecutablePath, ReadOnly = true, Width = 512, Margin = new Padding(0, 0, 0, 18) });
        _enabled.Checked = rule.Enabled;
        _children.Checked = rule.IncludeChildren;
        _custom.Checked = rule.UseCustomCloseZone;
        foreach (var control in new[] { _enabled, _children, _custom })
        {
            control.Margin = new Padding(0, 0, 0, 14);
            stack.Controls.Add(control);
        }
        stack.Controls.Add(new Label { AutoSize = false, Width = 512, Height = 94,
            Text = "强制结束可能丢失未保存内容、后台下载或同步任务。请先在无重要数据的应用中验证。\n\n自绘兼容将右上角 48 × 40 个逻辑像素视为关闭区域，并非所有应用都适用。仍有任何窗口时不会执行。",
            ForeColor = Color.FromArgb(125, 75, 30) });
        var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(0, 5, 18, 10) };
        var save = new Button { Text = "保存规则", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        footer.Controls.AddRange([save, cancel]);
        Controls.Add(stack);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
