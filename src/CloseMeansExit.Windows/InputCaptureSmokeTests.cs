using CloseMeansExit.Core;
using CloseMeansExit.Windows.Native;

namespace CloseMeansExit.Windows;

/// <summary>Exercises delayed capture completion without sending input to any desktop app.</summary>
internal static class InputCaptureSmokeTests
{
    internal static void Run()
    {
        using var input = new CloseInputMonitor(new WindowsPlatform(), _ => null);
        var received = new TaskCompletionSource<CloseObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        input.Observed += observation => received.TrySetResult(observation);
        var capture = new TaskCompletionSource<CloseInputMonitor.CapturedIntent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var at = DateTimeOffset.UtcNow;
        var owner = new ProcessIdentity(12345, 1, @"C:\Fixture\helper.exe");
        var target = new ProcessIdentity(12344, 1, @"C:\Fixture\app.exe");
        var press = new CloseInputMonitor.PressCandidate(0, owner.Pid, CloseEvidence.StandardCloseButton, at)
        {
            CaptureTask = capture.Task
        };
        var releasedAt = at.AddMilliseconds(5);
        Task completion = Task.Run(() => input.CompleteMousePressAsync(press, releasedAt));
        if (completion.Wait(50) || received.Task.IsCompleted)
            throw new InvalidOperationException("快速点击不能丢弃尚在核验中的身份捕获。");
        capture.SetResult(new CloseInputMonitor.CapturedIntent(target, owner, "Fixture"));
        completion.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        CloseObservation result = received.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        if (result.Process != target || result.At != releasedAt || result.Evidence != CloseEvidence.StandardCloseButton ||
            result.SourceWindow is not { } source || source.Handle != press.Hwnd || source.Owner != owner)
            throw new InvalidOperationException("延迟完成的点击必须保留目标身份及实际输入时间，不能采用核验完成时间。");
    }
}
