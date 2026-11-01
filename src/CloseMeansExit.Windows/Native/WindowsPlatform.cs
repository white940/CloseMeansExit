using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CloseMeansExit.Core;
using Microsoft.Win32.SafeHandles;

namespace CloseMeansExit.Windows.Native;

/// <summary>
/// Holds process handles during validation and termination. A recycled numeric PID
/// must never turn an old close observation into a request against a new process.
/// </summary>
public sealed class WindowsPlatform
{
    private readonly string _windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly string _ownExecutable = Environment.ProcessPath ?? "";

    public ProcessIdentity? TryGetIdentity(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId)
        {
            return null;
        }

        using var handle = NativeMethods.OpenProcess(NativeMethods.QueryLimitedInformation | NativeMethods.Synchronize, false, pid);
        return !handle.IsInvalid && IsAlive(handle) ? ReadIdentity(handle, pid) : null;
    }

    public ProcessIdentity ResolveApplicationProcess(ProcessIdentity owner)
    {
        if (IsProtected(owner)) return owner;
        ProcessIdentity target = ApplicationTargetResolver.Resolve(owner);
        return IsProtected(target) ? owner : target;
    }

    public IReadOnlyList<ProcessIdentity> ListRuleProcesses(IEnumerable<string> executablePaths)
    {
        var paths = executablePaths.Select(RulePaths.Normalize).Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<int, ProcessIdentity>();
        foreach (string name in paths.Select(path => Path.GetFileNameWithoutExtension(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Names reduce enumeration work only. Every candidate must match
            // the complete configured path and a verified process identity.
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    ProcessIdentity? identity = TryGetIdentity(process.Id);
                    if (identity is not null && paths.Contains(identity.ExecutablePath) && !IsProtected(identity))
                        result[identity.Pid] = identity;
                }
            }
        }
        return result.Values.ToArray();
    }

    public IReadOnlyList<WindowEntry> ListWindows()
    {
        var result = new List<WindowEntry>();
        var identities = new Dictionary<int, ProcessIdentity?>();
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint rawPid);
            int pid = unchecked((int)rawPid);
            if (pid <= 0 || pid == Environment.ProcessId)
            {
                return true;
            }

            if (!identities.TryGetValue(pid, out ProcessIdentity? identity))
            {
                identity = TryGetIdentity(pid);
                if (identity is not null) identity = ResolveApplicationProcess(identity);
                identities[pid] = identity;
            }

            if (identity is null || IsProtected(identity))
            {
                return true;
            }

            using var handle = NativeMethods.OpenProcess(NativeMethods.QueryLimitedInformation, false, identity.Pid);
            long memory = handle.IsInvalid ? 0 : ReadWorkingSet(handle);
            result.Add(new WindowEntry(hwnd, identity, GetWindowTitle(hwnd), memory));
            return true;
        };
        if (!NativeMethods.EnumWindows(callback, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举应用窗口；本轮检测已取消。");
        }

        GC.KeepAlive(callback);
        return result;
    }

    public ProcessSnapshot Probe(ProcessIdentity identity, bool includeChildren, CloseWindowSource? sourceWindow = null)
    {
        using var rootHandle = NativeMethods.OpenProcess(NativeMethods.QueryLimitedInformation | NativeMethods.Synchronize, false, identity.Pid);
        if (rootHandle.IsInvalid)
        {
            // ERROR_INVALID_PARAMETER means that this PID no longer exists. Other
            // failures (notably access denied) must not be treated as safe to kill.
            bool gone = Marshal.GetLastWin32Error() == 87;
            return new ProcessSnapshot(!gone, false, true, gone, 0);
        }

        if (!IsAlive(rootHandle))
        {
            return new ProcessSnapshot(false, true, false, true, 0);
        }

        ProcessIdentity? actual = ReadIdentity(rootHandle, identity.Pid);
        if (actual is null)
        {
            return new ProcessSnapshot(true, false, true, false, 0);
        }

        if (!SameIdentity(identity, actual))
        {
            return new ProcessSnapshot(true, false, true, true, 0);
        }

        long memory = ReadWorkingSet(rootHandle);
        if (IsProtected(actual))
        {
            return new ProcessSnapshot(true, true, true, false, memory);
        }

        var members = new List<HeldProcess>();
        try
        {
            // Window safety always covers the current descendant tree. The
            // option controls termination scope, not which windows get protected.
            if (!TryOpenGroup(identity, true, false, members, out string diagnostic))
            {
                return new ProcessSnapshot(true, true, true, false, memory) { Diagnostic = diagnostic };
            }

            bool reliable = TryInspectVisibleWindows(members, sourceWindow, out bool visible, out bool onlySourceWindow);
            return new ProcessSnapshot(true, true, visible, reliable, memory)
            {
                OnlySourceWindowVisible = onlySourceWindow
            };
        }
        finally
        {
            DisposeMembers(members);
        }
    }

    public bool TryTerminate(ProcessIdentity identity, bool includeChildren, out string error)
    {
        var members = new List<HeldProcess>();
        var guardMembers = new List<HeldProcess>();
        try
        {
            if (!TryOpenGroup(identity, includeChildren, true, members, out error))
            {
                return false;
            }

            // Even when only the root is to be ended, a document, call or save
            // dialog in one of its child processes must protect the whole app.
            // Query-only handles do not broaden the requested termination scope.
            if (!includeChildren && !TryOpenGroup(identity, true, false, guardMembers, out error))
            {
                return false;
            }

            var pids = members.Concat(guardMembers).Select(m => m.Identity.Pid).ToHashSet();
            if (!TryFindVisibleWindows(pids, out bool visible) || visible)
            {
                error = visible ? "应用仍有可见窗口（含最小化窗口或保存对话框），已取消结束。" : "窗口枚举失败，已取消结束。";
                return false;
            }

            if (!IsAlive(members[0].Handle))
            {
                error = "原进程已自然退出，已取消此次结束操作。";
                return false;
            }

            // Validate the whole captured group before making any change.
            foreach (HeldProcess member in members.Concat(guardMembers))
            {
                if (!IsAlive(member.Handle))
                {
                    continue;
                }

                ProcessIdentity? current = ReadIdentity(member.Handle, member.Identity.Pid);
                if (current is null || !SameIdentity(current, member.Identity))
                {
                    error = "进程身份无法确认，已取消结束。";
                    return false;
                }
            }

            // Guard-only descendants are inspected for windows, never ended.
            // Critical-process checks apply only to the actual termination set.
            foreach (HeldProcess member in members)
            {
                if (IsAlive(member.Handle) &&
                    (!NativeMethods.IsProcessCritical(member.Handle, out bool critical) || critical))
                {
                    error = "待结束进程的关键进程状态无法确认，已取消结束。";
                    return false;
                }
            }

            // Deepest children first. This is a fixed, handle-backed snapshot:
            // newly spawned/restarted processes are deliberately not chased.
            foreach (HeldProcess member in members.OrderByDescending(m => m.Depth))
            {
                if (!IsAlive(member.Handle))
                {
                    continue;
                }

                if (!TryFindVisibleWindows(pids, out visible) || visible)
                {
                    error = "结束前检测到窗口重新出现或枚举失败，已停止后续操作。";
                    return false;
                }

                if (!NativeMethods.TerminateProcess(member.Handle, 0))
                {
                    if (!IsAlive(member.Handle))
                    {
                        continue;
                    }

                    error = $"无法结束 PID {member.Identity.Pid}：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    return false;
                }
            }

            error = "";
            return true;
        }
        finally
        {
            DisposeMembers(members);
            DisposeMembers(guardMembers);
        }
    }

    internal static string GetWindowTitle(nint hwnd)
    {
        var text = new StringBuilder(1024);
        NativeMethods.GetWindowText(hwnd, text, text.Capacity);
        return text.Length == 0 ? "（无标题窗口）" : text.ToString();
    }

    private bool TryOpenGroup(ProcessIdentity identity, bool includeChildren, bool terminating, List<HeldProcess> members, out string error)
    {
        error = "";
        if (IsProtected(identity))
        {
            error = "系统目录、工具自身或其他会话中的进程不允许结束。";
            return false;
        }

        uint access = NativeMethods.QueryLimitedInformation | NativeMethods.Synchronize;
        if (terminating)
        {
            access |= NativeMethods.Terminate;
        }

        SafeProcessHandle rootHandle = NativeMethods.OpenProcess(access, false, identity.Pid);
        ProcessIdentity? actual = rootHandle.IsInvalid ? null : ReadIdentity(rootHandle, identity.Pid);
        if (actual is null || !SameIdentity(actual, identity) || !IsAlive(rootHandle))
        {
            rootHandle.Dispose();
            error = "原进程已退出、身份发生变化或权限不足，已取消结束。";
            return false;
        }

        members.Add(new HeldProcess(actual, rootHandle, 0, 0));
        if (!includeChildren)
        {
            return true;
        }

        if (!TryReadProcessParents(out Dictionary<int, int>? parents))
        {
            error = "无法读取进程树，已取消结束。";
            return false;
        }

        var queue = new Queue<HeldProcess>();
        var seen = new HashSet<int> { identity.Pid };
        queue.Enqueue(members[0]);
        while (queue.TryDequeue(out HeldProcess? parent))
        {
            foreach ((int childPid, int parentPid) in parents)
            {
                if (parentPid != parent.Identity.Pid || !seen.Add(childPid))
                {
                    continue;
                }

                SafeProcessHandle handle = NativeMethods.OpenProcess(access, false, childPid);
                ProcessIdentity? child = handle.IsInvalid ? null : ReadIdentity(handle, childPid);
                if (child is null)
                {
                    int nativeError = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (nativeError == 87)
                    {
                        continue; // A short-lived helper disappeared after the snapshot.
                    }

                    error = $"无法确认子进程 PID {childPid} 的身份或权限，已取消结束。";
                    return false;
                }

                if (!IsAlive(handle))
                {
                    handle.Dispose();
                    continue;
                }

                // A stale parent PID from Toolhelp must not attach an older,
                // unrelated process to a recently recycled parent PID.
                if (child.StartTimeUtcTicks < parent.Identity.StartTimeUtcTicks)
                {
                    handle.Dispose();
                    continue;
                }

                // System helpers are common descendants of desktop apps. They
                // still participate in read-only window protection. Being safe
                // to inspect never makes them eligible for termination.
                if (!IsInOwnSession(child.Pid) || (terminating && IsProtected(child)))
                {
                    handle.Dispose();
                    error = "进程树含系统目录、工具自身或其他会话中的进程，已取消结束。";
                    return false;
                }

                var held = new HeldProcess(child, handle, parent.Depth + 1, parent.Identity.Pid);
                members.Add(held);
                queue.Enqueue(held);
            }
        }

        // A PID in the first Toolhelp snapshot could have exited and been
        // recycled before we opened its handle. Confirm the parent relationship
        // again after holding all handles, rather than trusting that stale PID.
        if (!TryReadProcessParents(out Dictionary<int, int>? confirmedParents))
        {
            error = "无法再次确认进程树，已取消结束。";
            return false;
        }

        foreach (HeldProcess member in members.Skip(1))
        {
            if (IsAlive(member.Handle) &&
                (!confirmedParents.TryGetValue(member.Identity.Pid, out int confirmedParent) || confirmedParent != member.ParentPid))
            {
                error = "进程树成员的父进程发生变化，已取消结束。";
                return false;
            }
        }

        return true;
    }

    private bool IsProtected(ProcessIdentity identity)
    {
        if (identity.Pid <= 4 || identity.Pid == Environment.ProcessId ||
            string.IsNullOrWhiteSpace(_windowsDirectory) || string.IsNullOrWhiteSpace(_ownExecutable) ||
            RulePaths.IsProtectedPath(identity.ExecutablePath, _windowsDirectory, _ownExecutable))
        {
            return true;
        }

        return !IsInOwnSession(identity.Pid);
    }

    private static bool IsInOwnSession(int pid) =>
        NativeMethods.ProcessIdToSessionId((uint)pid, out uint targetSession) &&
        NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out uint ownSession) && targetSession == ownSession;

    internal static ProcessIdentity? ReadIdentity(SafeProcessHandle handle, int pid)
    {
        if (!NativeMethods.GetProcessTimes(handle, out NativeMethods.FileTime created, out _, out _, out _))
        {
            return null;
        }

        var path = new StringBuilder(32768);
        int length = path.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(handle, 0, path, ref length))
        {
            return null;
        }

        try
        {
            return new ProcessIdentity(pid, DateTime.FromFileTimeUtc(created.ToLong()).Ticks, RulePaths.Normalize(path.ToString()));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool SameIdentity(ProcessIdentity first, ProcessIdentity second) =>
        first.Pid == second.Pid && first.StartTimeUtcTicks == second.StartTimeUtcTicks &&
        RulePaths.Equal(first.ExecutablePath, second.ExecutablePath);

    private static bool IsAlive(SafeProcessHandle process) => NativeMethods.WaitForSingleObject(process, 0) == NativeMethods.WaitTimeout;

    private static long ReadWorkingSet(SafeProcessHandle process)
    {
        var counters = new NativeMethods.ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessMemoryCounters>() };
        return NativeMethods.GetProcessMemoryInfo(process, ref counters, counters.Size) ? checked((long)counters.WorkingSetSize) : 0;
    }

    private static bool TryFindVisibleWindows(HashSet<int> pids, out bool visible)
    {
        bool found = false;
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd))
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pids.Contains(unchecked((int)pid)))
                {
                    found = true;
                }
            }

            return true;
        };
        bool reliable = NativeMethods.EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        visible = found;
        return reliable;
    }

    private static bool TryInspectVisibleWindows(IReadOnlyList<HeldProcess> members, CloseWindowSource? sourceWindow,
        out bool visible, out bool onlySourceWindow)
    {
        var pids = members.Select(m => m.Identity.Pid).ToHashSet();
        int visibleCount = 0;
        nint soleWindow = 0;
        bool uncertainWindow = false;
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (threadId == 0 || pid == 0)
            {
                uncertainWindow = true;
                return true;
            }
            if (pids.Contains(unchecked((int)pid)))
            {
                // Every visible tree window protects the app, including modal,
                // disabled, tool and minimized windows. None are filtered out.
                visibleCount++;
                soleWindow = hwnd;
            }
            return true;
        };
        bool reliable = NativeMethods.EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        visible = visibleCount != 0;
        onlySourceWindow = false;
        reliable &= !uncertainWindow;
        if (!reliable || uncertainWindow || visibleCount != 1 || sourceWindow is not { Handle: not 0 } ||
            soleWindow != sourceWindow.Handle) return reliable;

        HeldProcess? owner = members.FirstOrDefault(m => SameIdentity(m.Identity, sourceWindow.Owner));
        if (owner is null || !IsAlive(owner.Handle) ||
            ReadIdentity(owner.Handle, owner.Identity.Pid) is not { } actualOwner ||
            !SameIdentity(actualOwner, sourceWindow.Owner)) return reliable;

        // This grants extra observation time only. Termination still rejects
        // every visible window through the independent check above.
        nint source = sourceWindow.Handle;
        onlySourceWindow = NativeMethods.IsWindow(source) && NativeMethods.IsWindowVisible(source) &&
            NativeMethods.GetAncestor(source, NativeMethods.GaRoot) == source &&
            NativeMethods.GetWindowThreadProcessId(source, out uint sourcePid) != 0 &&
            sourcePid == (uint)sourceWindow.Owner.Pid &&
            !NativeMethods.IsIconic(source) && NativeMethods.IsWindowEnabled(source);
        return reliable;
    }

    internal static bool TryReadProcessParents(out Dictionary<int, int> parents)
    {
        parents = [];
        nint snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == -1)
        {
            return false;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry>(),
                ExecutableName = ""
            };
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return false;
            }

            do
            {
                int pid = unchecked((int)entry.ProcessId);
                if (pid > 0)
                {
                    parents[pid] = unchecked((int)entry.ParentProcessId);
                }
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));

            return Marshal.GetLastWin32Error() == 18; // ERROR_NO_MORE_FILES
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    private static void DisposeMembers(IEnumerable<HeldProcess> members)
    {
        foreach (HeldProcess member in members)
        {
            member.Handle.Dispose();
        }
    }

    private sealed record HeldProcess(ProcessIdentity Identity, SafeProcessHandle Handle, int Depth, int ParentPid);
}
