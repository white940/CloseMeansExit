using System.ComponentModel;
using System.Runtime.InteropServices;
using CloseMeansExit.Core;

namespace CloseMeansExit.Windows.Native;

/// <summary>
/// Observes input without suppressing or changing it. A dedicated message loop
/// keeps the low-level hooks independent from the manager window's UI work.
/// </summary>
public sealed class CloseInputMonitor : IDisposable
{
    private readonly WindowsPlatform _platform;
    private readonly Func<string, AppRule?> _lookupRule;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private nint _mouseHook;
    private nint _keyboardHook;
    private Exception? _startError;
    private PressCandidate? _press;
    private volatile bool _disposed;
    private long _lastAltF4Ticks;

    public event Action<CloseObservation>? Observed;
    public event Action<string>? Diagnostic;

    public CloseInputMonitor(WindowsPlatform platform, Func<string, AppRule?> lookupRule)
    {
        _platform = platform;
        _lookupRule = lookupRule;
        _mouseCallback = MouseCallback;
        _keyboardCallback = KeyboardCallback;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "CloseMeansExit.Input"
        };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new TimeoutException("关闭按钮监测启动超时。");
        }

        if (_startError is not null)
        {
            throw new InvalidOperationException("无法启动关闭按钮监测。", _startError);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, 0, 0);
        }

        if (_thread is not null && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        // The loop owns unhooking. The delegates stay strongly referenced until
        // it exits, including the rare case of a slow thread during shutdown.
        GC.SuppressFinalize(this);
    }

    private void MessageLoop()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, 0, 0, 0, 0);
            if (_disposed)
            {
                return;
            }

            nint module = NativeMethods.GetModuleHandle(null);
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseCallback, module, 0);
            if (_mouseHook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册鼠标监测。");
            }

            _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardCallback, module, 0);
            if (_keyboardHook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册 Alt+F4 监测。");
            }

            _ready.Set();
            int result;
            while ((result = NativeMethods.GetMessage(out NativeMethods.Message message, 0, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }

            if (result == -1)
            {
                Report("关闭按钮监测消息循环异常退出。");
            }
        }
        catch (Exception error)
        {
            _startError = error;
            Report(error.Message);
        }
        finally
        {
            if (_mouseHook != 0)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
            }

            if (_keyboardHook != 0)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            }

            _ready.Set();
        }
    }

    private nint MouseCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !_disposed)
        {
            try
            {
                uint message = unchecked((uint)wParam);
                if (message is NativeMethods.WmLButtonDown or NativeMethods.WmLButtonUp)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MouseHookData>(lParam);
                    if (message == NativeMethods.WmLButtonDown)
                    {
                        BeginMousePress(data.Point);
                    }
                    else
                    {
                        EndMousePress(data.Point);
                    }
                }
            }
            catch (Exception error)
            {
                _press = null;
                Report($"本次点击未能识别：{error.Message}");
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void BeginMousePress(NativeMethods.Point point)
    {
        _press = null;
        nint hwnd = RootWindowAt(point);
        if (hwnd == 0)
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid <= 4 || pid == Environment.ProcessId)
        {
            return;
        }

        CloseEvidence? evidence = HitClose(hwnd, point);
        if (evidence is null)
        {
            return;
        }

        var press = new PressCandidate(hwnd, unchecked((int)pid), evidence.Value, DateTimeOffset.UtcNow);
        _press = press;
        // No process enumeration, process opening, executable-path lookup or
        // rule/file IO happens inside the hook callback.
        press.CaptureTask = Task.Run(() =>
        {
            try
            {
                return Capture(press.Hwnd, press.Pid, press.Evidence, press.At);
            }
            catch (Exception error)
            {
                Report($"本次关闭意图未能确认：{error.Message}");
                return null;
            }
        });
    }

    private void EndMousePress(NativeMethods.Point point)
    {
        PressCandidate? press = _press;
        _press = null;
        if (press is null || RootWindowAt(point) != press.Hwnd ||
            HitClose(press.Hwnd, point) != press.Evidence)
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(press.Hwnd, out uint pid);
        if (unchecked((int)pid) != press.Pid)
        {
            return;
        }

        // Validate the release position now, but finish identity capture off the
        // hook thread. A quick click must not lose a valid in-flight capture.
        _ = CompleteMousePressAsync(press, DateTimeOffset.UtcNow);
    }

    internal async Task CompleteMousePressAsync(PressCandidate press, DateTimeOffset releasedAt)
    {
        try
        {
            if (press.CaptureTask is not { } capture) return;
            CapturedIntent? captured = await capture.WaitAsync(TimeSpan.FromSeconds(2));
            if (_disposed || captured is null) return;
            if (NativeMethods.IsWindow(press.Hwnd))
            {
                NativeMethods.GetWindowThreadProcessId(press.Hwnd, out uint ownerPid);
                if (unchecked((int)ownerPid) != captured.WindowOwner.Pid) return;
                ProcessIdentity? actualOwner = _platform.TryGetIdentity(captured.WindowOwner.Pid);
                if (actualOwner is null || actualOwner.StartTimeUtcTicks != captured.WindowOwner.StartTimeUtcTicks ||
                    !RulePaths.Equal(actualOwner.ExecutablePath, captured.WindowOwner.ExecutablePath)) return;
            }
            // Preserve the actual input time: a later rule change cannot arm
            // a click merely because its asynchronous capture completed later.
            Publish(new CloseObservation(captured.Process, captured.Title, press.Evidence, releasedAt));
        }
        catch (TimeoutException) { Report("本次关闭意图的身份核验超时，已放弃该信号。"); }
        catch (Exception error) { Report($"本次关闭意图未能完成：{error.Message}"); }
    }

    private nint KeyboardCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !_disposed)
        {
            try
            {
                uint message = unchecked((uint)wParam);
                if (message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(lParam);
                    if (data.VirtualKey == 0x73 && (data.Flags & 0x20) != 0)
                    {
                        DateTimeOffset at = DateTimeOffset.UtcNow;
                        if (at.UtcTicks - _lastAltF4Ticks >= TimeSpan.TicksPerSecond)
                        {
                            _lastAltF4Ticks = at.UtcTicks;
                            nint hwnd = NativeMethods.GetForegroundWindow();
                            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                            if (hwnd != 0 && pid > 4 && pid != Environment.ProcessId)
                            {
                                _ = Task.Run(() =>
                                {
                                    try
                                    {
                                        CapturedIntent? captured = Capture(hwnd, unchecked((int)pid), CloseEvidence.AltF4, at);
                                        if (captured is not null)
                                        {
                                            Publish(new CloseObservation(captured.Process, captured.Title, CloseEvidence.AltF4, at));
                                        }
                                    }
                                    catch (Exception error)
                                    {
                                        Report($"本次 Alt+F4 未能确认：{error.Message}");
                                    }
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception error)
            {
                Report($"本次按键未能识别：{error.Message}");
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private CapturedIntent? Capture(nint hwnd, int pid, CloseEvidence evidence, DateTimeOffset at)
    {
        if (_disposed)
        {
            return null;
        }

        ProcessIdentity? owner = _platform.TryGetIdentity(pid);
        // A replacement process can reuse a PID, but cannot have started before
        // the already-captured input. Never adopt a process started after it.
        if (owner is null || owner.StartTimeUtcTicks > at.UtcTicks)
        {
            return null;
        }

        if (NativeMethods.IsWindow(hwnd))
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint currentPid);
            if (unchecked((int)currentPid) != pid)
            {
                return null;
            }
        }

        ProcessIdentity identity = _platform.ResolveApplicationProcess(owner);
        if (evidence == CloseEvidence.CustomTitleBarZone)
        {
            AppRule? rule = _lookupRule(identity.ExecutablePath);
            if (rule is not { Enabled: true, UseCustomCloseZone: true })
            {
                return null;
            }
        }

        return new CapturedIntent(identity, owner, WindowsPlatform.GetWindowTitle(hwnd));
    }

    private static nint RootWindowAt(NativeMethods.Point point)
    {
        nint hwnd = NativeMethods.WindowFromPoint(point);
        return hwnd == 0 ? 0 : NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
    }

    private static CloseEvidence? HitClose(nint hwnd, NativeMethods.Point point)
    {
        nint packedPoint = unchecked((nint)((point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16)));
        nint delivered = NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WmNcHitTest, 0, packedPoint,
            NativeMethods.SmtoAbortIfHung, 30, out nuint result);
        if (delivered == 0)
        {
            return null;
        }

        int hit = unchecked((int)result);
        if (hit == NativeMethods.HtClose)
        {
            return CloseEvidence.StandardCloseButton;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.Rect rect))
        {
            return null;
        }

        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        return ClassifyCloseHit(hit, point, rect, dpi);
    }

    internal static CloseEvidence? ClassifyCloseHit(int hit, NativeMethods.Point point, NativeMethods.Rect rect, uint dpi)
    {
        if (hit == NativeMethods.HtClose) return CloseEvidence.StandardCloseButton;
        // SDL/CEF title bars can report either client or caption for a drawn
        // close button. The per-rule opt-in is still enforced by Capture.
        if (hit is not (NativeMethods.HtClient or NativeMethods.HtCaption)) return null;
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        int width = (int)Math.Ceiling(48 * scale);
        int height = (int)Math.Ceiling(40 * scale);
        return point.X >= rect.Right - width && point.X < rect.Right &&
            point.Y >= rect.Top && point.Y < rect.Top + height
                ? CloseEvidence.CustomTitleBarZone
                : null;
    }

    private void Publish(CloseObservation observation)
    {
        _ = Task.Run(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Observed?.Invoke(observation);
            }
            catch (Exception error)
            {
                Report($"关闭观察处理失败：{error.Message}");
            }
        });
    }

    private void Report(string message)
    {
        _ = Task.Run(() =>
        {
            if (!_disposed)
            {
                try
                {
                    Diagnostic?.Invoke(message);
                }
                catch
                {
                    // Subscriber failures cannot escape an unmanaged hook.
                }
            }
        });
    }

    internal sealed record CapturedIntent(ProcessIdentity Process, ProcessIdentity WindowOwner, string Title);

    internal sealed class PressCandidate(nint hwnd, int pid, CloseEvidence evidence, DateTimeOffset at)
    {
        internal nint Hwnd { get; } = hwnd;
        internal int Pid { get; } = pid;
        internal CloseEvidence Evidence { get; } = evidence;
        internal DateTimeOffset At { get; } = at;
        internal Task<CapturedIntent?>? CaptureTask;
    }
}
