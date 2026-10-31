using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--notification-demo"))
        {
            return NotificationDemo.Run();
        }
        if (args.Contains("--smoke-test"))
        {
            return SmokeTests.Run();
        }
        if (args.Contains("--fixture"))
        {
            Application.Run(new TestFixtureForm(args.Contains("--fixture-secondary")));
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\CloseMeansExit.Desktop", out bool first);
        if (!first)
        {
            MessageBox.Show("叉掉就退已在运行，请从通知区域打开。切换版本时，请先从托盘退出旧版。", "叉掉就退");
            return 0;
        }

        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloseMeansExit");
        Directory.CreateDirectory(dataDirectory);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"操作未完成：{e.Exception.Message}", "叉掉就退", MessageBoxButtons.OK, MessageBoxIcon.Error);
        using var form = new MainForm(dataDirectory, args.Contains("--tray"));
        Application.Run(form);
        return 0;
    }
}
