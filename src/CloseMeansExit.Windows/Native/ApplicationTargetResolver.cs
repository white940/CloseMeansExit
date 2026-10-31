using CloseMeansExit.Core;
using Microsoft.Win32.SafeHandles;

namespace CloseMeansExit.Windows.Native;

/// <summary>
/// Resolves only explicitly known helper layouts along their verified native
/// parent chain. Any uncertain relationship leaves the window owner unchanged.
/// </summary>
internal static class ApplicationTargetResolver
{
    private const int MaximumParentLinks = 8;

    internal static ProcessIdentity Resolve(ProcessIdentity owner)
    {
        string? expected = AppTargets.ExpectedMainExecutable(owner.ExecutablePath);
        if (expected is null) return owner;

        var held = new List<HeldIdentity>();
        try
        {
            if (!NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out uint session)) return owner;
            HeldIdentity? capturedOwner = TryHold(owner.Pid, session, held);
            if (capturedOwner is null || !Same(owner, capturedOwner.Identity)) return owner;
            if (!WindowsPlatform.TryReadProcessParents(out var initialParents)) return owner;

            var seen = new HashSet<int> { owner.Pid };
            HeldIdentity current = capturedOwner;
            for (int link = 0; link < MaximumParentLinks; link++)
            {
                if (!initialParents.TryGetValue(current.Identity.Pid, out int parentPid) ||
                    parentPid <= 4 || !seen.Add(parentPid)) return owner;
                HeldIdentity? parent = TryHold(parentPid, session, held);
                if (parent is null || parent.Identity.StartTimeUtcTicks > current.Identity.StartTimeUtcTicks) return owner;

                if (RulePaths.Equal(parent.Identity.ExecutablePath, expected))
                {
                    return ValidateChain(held, session) ? parent.Identity : owner;
                }
                current = parent;
            }
            return owner;
        }
        finally
        {
            foreach (HeldIdentity member in held) member.Handle.Dispose();
        }
    }

    private static HeldIdentity? TryHold(int pid, uint session, List<HeldIdentity> held)
    {
        SafeProcessHandle handle = NativeMethods.OpenProcess(
            NativeMethods.QueryLimitedInformation | NativeMethods.Synchronize, false, pid);
        ProcessIdentity? identity = handle.IsInvalid ? null : WindowsPlatform.ReadIdentity(handle, pid);
        if (identity is null || NativeMethods.WaitForSingleObject(handle, 0) != NativeMethods.WaitTimeout ||
            !NativeMethods.ProcessIdToSessionId((uint)pid, out uint actualSession) || actualSession != session)
        {
            handle.Dispose();
            return null;
        }
        var member = new HeldIdentity(identity, handle);
        held.Add(member);
        return member;
    }

    private static bool ValidateChain(IReadOnlyList<HeldIdentity> held, uint session)
    {
        if (!WindowsPlatform.TryReadProcessParents(out var confirmedParents)) return false;
        for (int index = 0; index < held.Count; index++)
        {
            HeldIdentity member = held[index];
            if (NativeMethods.WaitForSingleObject(member.Handle, 0) != NativeMethods.WaitTimeout ||
                !NativeMethods.ProcessIdToSessionId((uint)member.Identity.Pid, out uint currentSession) || currentSession != session)
                return false;
            ProcessIdentity? actual = WindowsPlatform.ReadIdentity(member.Handle, member.Identity.Pid);
            if (actual is null || !Same(actual, member.Identity)) return false;
            if (index + 1 < held.Count &&
                (!confirmedParents.TryGetValue(member.Identity.Pid, out int parentPid) || parentPid != held[index + 1].Identity.Pid))
                return false;
        }
        return true;
    }

    private static bool Same(ProcessIdentity first, ProcessIdentity second) =>
        first.Pid == second.Pid && first.StartTimeUtcTicks == second.StartTimeUtcTicks &&
        RulePaths.Equal(first.ExecutablePath, second.ExecutablePath);

    private sealed record HeldIdentity(ProcessIdentity Identity, SafeProcessHandle Handle);
}
