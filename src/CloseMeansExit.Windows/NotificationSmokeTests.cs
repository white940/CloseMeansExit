using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

/// <summary>Exercises notification choices without inspecting or ending any process.</summary>
internal static class NotificationSmokeTests
{
    internal static void Run()
    {
        InputCaptureSmokeTests.Run();
        var rect = new NativeMethods.Rect { Left = 100, Top = 100, Right = 900, Bottom = 700 };
        var closePoint = new NativeMethods.Point { X = 875, Y = 125 };
        Require(CloseInputMonitor.ClassifyCloseHit(NativeMethods.HtClient, closePoint, rect, 96) == CloseEvidence.CustomTitleBarZone,
            "客户区绘制的关闭区域没有识别。");
        Require(CloseInputMonitor.ClassifyCloseHit(NativeMethods.HtCaption, closePoint, rect, 96) == CloseEvidence.CustomTitleBarZone,
            "SDL标题栏绘制的关闭区域没有识别。");
        Require(CloseInputMonitor.ClassifyCloseHit(NativeMethods.HtCaption,
            new NativeMethods.Point { X = 800, Y = 125 }, rect, 96) is null,
            "普通标题栏拖动区域不应成为关闭区域。");
        Require(CloseInputMonitor.ClassifyCloseHit(8, closePoint, rect, 96) is null &&
            CloseInputMonitor.ClassifyCloseHit(9, closePoint, rect, 96) is null,
            "最小化或最大化按钮不能成为兼容关闭信号。");
        Require(CloseInputMonitor.ClassifyCloseHit(NativeMethods.HtCaption,
            new NativeMethods.Point { X = 840, Y = 150 }, rect, 144) == CloseEvidence.CustomTitleBarZone,
            "150% DPI下的兼容区域未缩放。");
        var observation = new CloseObservation(
            new ProcessIdentity(12345, 1, @"C:\Apps\notice-fixture.exe"),
            "Notice fixture", CloseEvidence.WindowDisappeared, DateTimeOffset.UtcNow);

        using (var notice = new ResidencyNoticeForm(observation, 64L * 1024 * 1024, false))
        {
            int adds = 0;
            int dismissals = 0;
            notice.AddRequested += () => adds++;
            notice.DismissRequested += () => dismissals++;
            Show(notice);
            string[] text = Descendants(notice).Select(c => c.Text).ToArray();
            Require(text.Contains("notice-fixture.exe"), "通知缺少应用文件名。");
            Require(text.Contains(observation.Process.ExecutablePath), "通知缺少应用完整路径。");
            Require(text.Any(t => t.Contains($"{64d:F1} MB", StringComparison.Ordinal)), "通知缺少进程工作集。");
            Require(text.Any(t => t.Contains("窗口已消失，进程仍在运行", StringComparison.Ordinal)), "窗口消失证据被错误描述为点叉。");
            Require(notice.TopMost && !notice.ShowInTaskbar, "通知应置顶且不显示任务栏按钮。");

            Button add = Button(notice, "加入黑名单");
            Button dismiss = Button(notice, "暂不处理");
            Require(text.Any(t => t.Contains("当前及以后的无窗口后台实例会自动结束", StringComparison.Ordinal)),
                "加入前应说明当前后台实例也会自动结束。");
            Require(!Descendants(notice).OfType<CheckBox>().Any(c => c.Text.Contains("自绘", StringComparison.Ordinal)) &&
                !notice.UseCustomCloseZone, "后台自动处理不应要求选择关闭按钮兼容。");
            Require(add.Enabled && dismiss.Enabled, "通知初始按钮不可用。");
            notice.SetPendingCount(2);
            Require(Descendants(notice).Any(c => c.Visible && c.Text == "另有 2 个应用等待确认"), "通知未显示其他待确认应用。");
            notice.SetPendingCount(0);
            Require(!Descendants(notice).Any(c => c.Visible && c.Text.Contains("个应用等待确认", StringComparison.Ordinal)), "清空队列后仍显示等待数量。");

            add.PerformClick();
            Require(adds == 1 && !add.Enabled && !dismiss.Enabled,
                "保存请求未禁用操作按钮。");
            add.PerformClick();
            dismiss.PerformClick();
            Require(adds == 1 && dismissals == 0 && !notice.IsDisposed, "保存中重复点击产生了额外操作。");
            notice.ShowSaveFailure("测试：磁盘暂时不可写");
            Require(add.Enabled && dismiss.Enabled && !notice.IsDisposed,
                "保存失败后通知未恢复重试。");
            Require(Descendants(notice).Any(c => c.Visible && c.Text.Contains("保存失败", StringComparison.Ordinal)), "保存失败信息不可见。");
            add.PerformClick();
            Require(adds == 2, "保存失败后不能再次请求加入。");
            notice.Close(); // The owner closes only after a successful save.
            Application.DoEvents();
            Require(dismissals == 0 && notice.IsDisposed, "保存成功关闭通知被误判为暂不处理。");
        }

        using (var notice = new ResidencyNoticeForm(observation, 0, false))
        {
            int dismissals = 0;
            notice.DismissRequested += () => dismissals++;
            Show(notice);
            Button(notice, "暂不处理").PerformClick();
            Application.DoEvents();
            Require(dismissals == 1 && notice.IsDisposed, "暂不处理必须关闭通知且只触发一次事件。");
        }

        using (var notice = new ResidencyNoticeForm(observation, 1, true))
        {
            int dismissals = 0;
            notice.DismissRequested += () => dismissals++;
            Show(notice);
            Require(Button(notice, "恢复启用").Enabled, "已有停用规则的通知缺少恢复启用操作。");
            Require(NativeMethods.SendMessageTimeout(notice.Handle, 0x0112, 0xF060, 0,
                NativeMethods.SmtoAbortIfHung, 3000, out _) != 0, "无法向测试通知发送关闭操作。");
            Application.DoEvents();
            Require(dismissals == 1 && notice.IsDisposed, "点叉必须按暂不处理关闭且只触发一次事件。");
        }

        using (var notice = new ResidencyNoticeForm(observation with { Evidence = CloseEvidence.StandardCloseButton }, 1, false))
        {
            Show(notice);
            Require(Descendants(notice).Any(c => c.Text.Contains("当前及以后的无窗口后台实例会自动结束", StringComparison.Ordinal)),
                "标准关闭来源也应明确加入后的后台自动处理行为。");
            Button(notice, "暂不处理").PerformClick();
            Application.DoEvents();
        }

        using (var notice = new ResidencyNoticeForm(observation with
        {
            Process = observation.Process with { ExecutablePath = @"C:\Apps\Steam\Steam.exe" }
        }, 1, false))
        {
            Show(notice);
            CheckBox children = Descendants(notice).OfType<CheckBox>().Single(c =>
                c.Text.StartsWith("同时结束子进程", StringComparison.Ordinal));
            Require(children.Visible && !children.Checked && !notice.IncludeChildren,
                "Steam子进程范围必须明确展示且默认关闭。");
            children.Checked = true;
            Require(notice.IncludeChildren, "Steam子进程选择没有返回给保存操作。");
            Button(notice, "暂不处理").PerformClick();
            Application.DoEvents();
        }

        using (var notice = new ResidencyNoticeForm(observation with
        {
            Process = observation.Process with { ExecutablePath = @"C:\Apps\Steam\Steam.exe" }
        }, 1, true, existingIncludeChildren: true))
        {
            Show(notice);
            Require(notice.IncludeChildren, "恢复规则时不得遗失已明确选择的子进程范围。");
            Button(notice, "暂不处理").PerformClick();
            Application.DoEvents();
        }

        var originalRule = new AppRule
        {
            ExecutablePath = observation.Process.ExecutablePath,
            IncludeChildren = true,
            UseCustomCloseZone = true
        };
        using (var dialog = new RuleDialog(originalRule))
        {
            Require(dialog.Rule.IncludeChildren && dialog.Rule.UseCustomCloseZone,
                "编辑规则时应保留原结束范围及旧配置字段。");
            Require(!Descendants(dialog).OfType<CheckBox>().Any(c => c.Text.Contains("自绘", StringComparison.Ordinal)),
                "后台自动处理不应要求用户设置自绘关闭区域。");
        }
    }

    private static void Show(ResidencyNoticeForm notice)
    {
        notice.Show();
        Application.DoEvents();
        Rectangle working = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(notice).WorkingArea;
        Require(working.Contains(notice.Bounds), "通知超出了屏幕工作区。");
    }

    private static Button Button(Control root, string text) =>
        Descendants(root).OfType<Button>().Single(b => b.Text == text);

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
