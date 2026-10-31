using CloseMeansExit.Core;

namespace CloseMeansExit.Windows;

internal static class NotificationDemo
{
    public static int Run()
    {
        // This is a synthetic UI demonstration, entirely separate from the
        // monitor, process APIs, registry and persistent settings.
        var observation = new CloseObservation(new ProcessIdentity(0, 0, @"C:\Example\BackgroundDemo.exe"),
            "演示应用", CloseEvidence.StandardCloseButton, DateTimeOffset.UtcNow);
        using var popup = new ResidencyNoticeForm(observation, 134_742_016, false);
        popup.AddRequested += () =>
        {
            popup.SetBusy(true);
            MessageBox.Show(popup, "演示：已选择加入黑名单。\n本模式不写入设置，也不操作任何真实进程。", "驻留提醒演示");
            popup.Close();
        };
        Application.Run(popup);
        return 0;
    }
}
