using CloseMeansExit.Core;

DateTimeOffset origin = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
ProcessIdentity process = new(42, 123456, @"C:\Apps\Example\Example.exe");
AppRule rule = new() { ExecutablePath = process.ExecutablePath };
ProcessSnapshot hidden = new(true, true, false, true, 100_000);
ProcessSnapshot visible = hidden with { HasVisibleWindows = true };
int passed = 0;

void Equal<T>(T expected, T actual, string description)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"FAIL {description}: expected {expected}, got {actual}");
    }

    passed++;
}

void Throws<T>(Action action, string description) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        passed++;
        return;
    }

    throw new InvalidOperationException($"FAIL {description}: expected {typeof(T).Name}");
}

CloseSession NewSession(AppRule? captured = null, CloseEvidence evidence = CloseEvidence.StandardCloseButton) =>
    new(new(process, "Example", evidence, origin), captured);

PolicyDecision Advance(CloseSession session, ProcessSnapshot snapshot, double seconds,
    AppRule? current = null, bool paused = false) =>
    session.Advance(snapshot, current, origin.AddSeconds(seconds), paused);

CloseSession eligible = NewSession(rule);
Equal(DecisionKind.Wait, Advance(eligible, hidden, 0, rule).Kind, "wait for close grace");
Equal(DecisionKind.Wait, Advance(eligible, hidden, 2.99, rule).Kind, "do not kill before three seconds");
Equal(DecisionKind.Terminate, Advance(eligible, hidden, 3, rule).Kind, "eligible hidden process ends");
Equal(DecisionKind.Ignore, Advance(eligible, hidden, 4, rule).Kind, "terminal action emitted once");

CloseSession candidate = NewSession();
Advance(candidate, hidden, 0);
Equal(DecisionKind.Candidate, Advance(candidate, hidden, 3).Kind, "unlisted application only candidate");

CloseSession noRetroactive = NewSession();
Advance(noRetroactive, hidden, 0);
Equal(DecisionKind.Candidate, Advance(noRetroactive, hidden, 3, rule).Kind, "new rule cannot arm past click");

CloseSession disabledAtClick = NewSession(rule with { Enabled = false });
Advance(disabledAtClick, hidden, 0, rule);
Equal(DecisionKind.Candidate, Advance(disabledAtClick, hidden, 3, rule).Kind, "disabled captured rule cannot arm");

CloseSession pathMismatch = NewSession(rule with { ExecutablePath = @"D:\Other\Example.exe" });
Advance(pathMismatch, hidden, 0, rule);
Equal(DecisionKind.Candidate, Advance(pathMismatch, hidden, 3, rule).Kind, "filename alone never matches");

CloseSession caseMatch = NewSession(rule with { ExecutablePath = @"c:/apps/example/EXAMPLE.exe" });
Advance(caseMatch, hidden, 0, rule);
Equal(DecisionKind.Terminate, Advance(caseMatch, hidden, 3, rule).Kind, "Windows path case is ignored");

CloseSession minimized = NewSession(rule);
Equal(DecisionKind.Wait, Advance(minimized, visible, 0, rule).Kind, "window initially may remain visible");
Equal(DecisionKind.Ignore, Advance(minimized, visible, 3, rule).Kind, "minimized or other visible window cancels");

CloseSession savePrompt = NewSession(rule);
Advance(savePrompt, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(savePrompt, visible, 1, rule).Kind, "save prompt or window returning cancels");
Equal(DecisionKind.Ignore, Advance(savePrompt, hidden, 3, rule).Kind, "canceled attempt cannot reactivate");

CloseSession brieflyHidden = NewSession(rule);
Advance(brieflyHidden, visible, 0, rule);
Equal(DecisionKind.Wait, Advance(brieflyHidden, hidden, 2.9, rule).Kind, "hidden stability begins at observation");
Equal(DecisionKind.Wait, Advance(brieflyHidden, hidden, 3, rule).Kind, "short hidden duration still waits");
Equal(DecisionKind.Terminate, Advance(brieflyHidden, hidden, 4, rule).Kind, "stable disappearance can terminate");

foreach ((ProcessSnapshot snapshot, string description) in new[]
{
    (hidden with { IsAlive = false }, "natural exit"),
    (hidden with { IdentityMatches = false }, "PID reused or start time/path changed"),
    (hidden with { Reliable = false }, "window enumeration failed")
})
{
    CloseSession session = NewSession(rule);
    Equal(DecisionKind.Ignore, Advance(session, snapshot, 0, rule).Kind, description);
}

CloseSession removed = NewSession(rule);
Advance(removed, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(removed, hidden, 3).Kind, "rule removed during grace");

CloseSession disabled = NewSession(rule);
Advance(disabled, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(disabled, hidden, 3, rule with { Enabled = false }).Kind, "rule disabled during grace");

CloseSession scopeChange = NewSession(rule);
Advance(scopeChange, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(scopeChange, hidden, 3, rule with { IncludeChildren = true }).Kind,
    "process tree scope change cancels past click");

CloseSession customScopeChange = NewSession(rule);
Advance(customScopeChange, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(customScopeChange, hidden, 3, rule with { UseCustomCloseZone = true }).Kind,
    "custom-title scope change cancels standard-close attempt too");

CloseSession paused = NewSession(rule);
Advance(paused, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(paused, hidden, 3, rule, paused: true).Kind, "pause cancels pending operation");

CloseSession expired = NewSession(rule);
Advance(expired, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(expired, hidden, 10, rule).Kind, "bounded lifetime prevents late termination");

CloseSession clockRollback = NewSession(rule);
Advance(clockRollback, hidden, 1, rule);
Equal(DecisionKind.Ignore, Advance(clockRollback, hidden, 0.5, rule).Kind, "clock rollback cancels");

CloseSession disappearance = NewSession(rule, CloseEvidence.WindowDisappeared);
Advance(disappearance, hidden, 0, rule);
Equal(DecisionKind.Candidate, Advance(disappearance, hidden, 3, rule).Kind, "heuristic cannot terminate even with rule");

CloseSession customOff = NewSession(rule, CloseEvidence.CustomTitleBarZone);
Equal(DecisionKind.Ignore, Advance(customOff, hidden, 0, rule).Kind, "custom title region requires opt-in");

AppRule customRule = rule with { UseCustomCloseZone = true };
CloseSession customOn = NewSession(customRule, CloseEvidence.CustomTitleBarZone);
Advance(customOn, hidden, 0, customRule);
Equal(DecisionKind.Terminate, Advance(customOn, hidden, 3, customRule).Kind, "explicit custom title opt-in");

CloseSession customRevoked = NewSession(customRule, CloseEvidence.CustomTitleBarZone);
Advance(customRevoked, hidden, 0, customRule);
Equal(DecisionKind.Ignore, Advance(customRevoked, hidden, 3, rule).Kind, "custom opt-in revoked");

CloseSession altF4 = NewSession(rule, CloseEvidence.AltF4);
Advance(altF4, hidden, 0, rule);
Equal(DecisionKind.Terminate, Advance(altF4, hidden, 3, rule).Kind, "Alt-F4 is explicit close evidence");

Equal(@"C:\Apps\Example\Example.exe", RulePaths.Normalize(@"c:/Apps/Other/../Example/./Example.exe"), "normalize Windows path");
Equal(true, RulePaths.Equal(@"\\?\C:\Apps\Example.exe", @"c:\apps\EXAMPLE.exe"), "extended drive prefix");
Equal(true, RulePaths.Equal(@"\\?\UNC\server\share\App.exe", @"\\SERVER\share\app.exe"), "extended UNC prefix");
Equal(false, RulePaths.Equal(@"C:\Apps\Example.exe", @"D:\Apps\Example.exe"), "separate executable paths");
Equal(true, RulePaths.IsProtectedPath(@"C:\Windows\System32\notepad.exe", @"c:\WINDOWS", @"C:\Tools\CloseMeansExit.exe"), "Windows directory protected");
Equal(false, RulePaths.IsProtectedPath(@"C:\WindowsElsewhere\app.exe", @"C:\Windows", @"C:\Tools\CloseMeansExit.exe"), "directory prefix requires boundary");
Equal(true, RulePaths.IsProtectedPath(@"c:\tools\CLOSEMEANSEXIT.exe", @"C:\Windows", @"C:\Tools\CloseMeansExit.exe"), "own process protected");
Equal(true, RulePaths.IsProtectedPath("app.exe", @"C:\Windows", @"C:\Tools\CloseMeansExit.exe"), "invalid target is protected");

foreach (string invalid in new[] { "app.exe", @"C:app.exe", @"C:\..\app.exe", @"C:\Apps\app.exe:stream", @"C:\Apps\name.\app.exe", @"C:\Apps\*.exe", @"\\.\pipe\sample" })
{
    Throws<ArgumentException>(() => RulePaths.Normalize(invalid), "reject unsafe path " + invalid);
}

string temporaryDirectory = Path.Combine(Path.GetTempPath(), "CloseMeansExit-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);

try
{
    string path = Path.Combine(temporaryDirectory, "settings.json");
    AppSettings empty = SettingsStore.Load(path, out string? firstWarning);
    Equal(0, empty.Rules.Count, "missing config starts empty");
    Equal<string?>(null, firstWarning, "missing config is not corruption");

    SettingsStore.Save(path, new AppSettings { Rules = [rule], StartWithWindows = true });
    AppSettings saved = SettingsStore.Load(path, out string? savedWarning);
    Equal(1, saved.Rules.Count, "settings rule round trip");
    Equal(rule, saved.Rules[0], "rule values survive save");
    Equal(true, saved.StartWithWindows, "startup choice survives save");
    Equal<string?>(null, savedWarning, "valid config has no warning");
    Equal(0, Directory.GetFiles(temporaryDirectory, "*.tmp").Length, "atomic write removes temporary file");

    File.WriteAllText(path, "{ invalid json");
    AppSettings recovered = SettingsStore.Load(path, out string? corruptWarning);
    Equal(0, recovered.Rules.Count, "corrupt config cannot arm any rule");
    Equal(true, !string.IsNullOrWhiteSpace(corruptWarning), "corruption has readable warning");
    string[] backups = Directory.GetFiles(temporaryDirectory, "settings.json.broken-*");
    Equal(1, backups.Length, "corrupt bytes preserved");
    Equal("{ invalid json", File.ReadAllText(backups[0]), "backup preserves exact input");
    Equal(false, File.Exists(path), "broken config moved aside before recovery");

    File.WriteAllText(path, "{\"Rules\":[{\"ExecutablePath\":\"relative.exe\"}]}");
    AppSettings invalidRule = SettingsStore.Load(path, out string? invalidWarning);
    Equal(0, invalidRule.Rules.Count, "invalid saved rule cannot arm");
    Equal(true, !string.IsNullOrWhiteSpace(invalidWarning), "invalid saved path is reported");

    File.WriteAllText(path, "{\"Rules\":null}");
    Equal(0, SettingsStore.Load(path, out _).Rules.Count, "null rule list treated as broken config");
}
finally
{
    Directory.Delete(temporaryDirectory, recursive: true);
}

ResidencyNotice Notice(string path, double seconds, long memory, int pid = 42) =>
    new(new(new(pid, 123456, path), $"Window at {seconds}", CloseEvidence.StandardCloseButton,
        origin.AddSeconds(seconds)), memory);

NotificationQueue notices = new();
ResidencyNotice firstNotice = Notice(@"C:\Apps\One.exe", 0, 100);
ResidencyNotice secondNotice = Notice(@"C:\Apps\Two.exe", 1, 200);
ResidencyNotice updatedFirst = Notice(@"c:/apps/ONE.exe", 2, 300, pid: 43);
Equal(0, notices.Count, "notice queue starts empty");
Equal(false, notices.TryDequeue(out ResidencyNotice? emptyNotice), "empty notice dequeue fails");
Equal<ResidencyNotice?>(null, emptyNotice, "empty notice dequeue clears output");
Equal(true, notices.Enqueue(firstNotice), "first path creates notice entry");
Equal(true, notices.Enqueue(secondNotice), "different path creates another entry");
Equal(false, notices.Enqueue(updatedFirst), "case and slash aliases coalesce notice");
Equal(2, notices.Count, "coalescing does not grow notice queue");
Equal(true, notices.TryDequeue(out ResidencyNotice? latestFirst), "updated first notice dequeues");
Equal(updatedFirst, latestFirst, "coalesced entry keeps position and latest observation/memory");
Equal(1, notices.Count, "dequeue decreases notice count");
Equal(true, notices.Enqueue(firstNotice), "same app can notify again after previous notice dequeued");
Equal(true, notices.TryDequeue(out ResidencyNotice? queuedSecond), "second entry stays ahead of requeued first");
Equal(secondNotice, queuedSecond, "notice queue preserves FIFO order");
Equal(true, notices.TryDequeue(out ResidencyNotice? requeuedFirst), "requeued same path dequeues");
Equal(firstNotice, requeuedFirst, "new lifecycle accepts same app without session suppression");

notices.Enqueue(updatedFirst);
Equal(false, notices.Enqueue(firstNotice), "late older observation still coalesces");
notices.TryDequeue(out ResidencyNotice? notRegressed);
Equal(updatedFirst, notRegressed, "late older event cannot replace latest observation/memory");
Equal(true, notices.Enqueue(Notice(@"D:\Apps\One.exe", 3, 400)), "same filename on different full path remains independent");
notices.Enqueue(firstNotice);
notices.Remove(@"c:/APPS/one.exe");
Equal(1, notices.Count, "remove matches normalized Windows path case-insensitively");
notices.TryDequeue(out ResidencyNotice? otherDrive);
Equal(@"D:\Apps\One.exe", otherDrive?.Observation.Process.ExecutablePath, "remove does not affect independent full path");
notices.Remove(@"C:\Missing\None.exe");
Equal(0, notices.Count, "removing absent path is harmless");
Equal(true, notices.Enqueue(firstNotice), "removed path can be enqueued again");
notices.Clear();
Equal(0, notices.Count, "clear removes all pending notices");
Equal(false, notices.TryDequeue(out _), "cleared queue has no notice");
Equal(true, notices.Enqueue(firstNotice), "clear also resets path deduplication");
notices.Clear();

for (int index = 0; index <= NotificationQueue.Capacity; index++)
{
    notices.Enqueue(Notice($@"C:\Apps\App{index}.exe", index, index));
}

Equal(50, notices.Count, "notice queue capacity is fifty");
notices.Enqueue(Notice(@"c:/apps/app1.exe", 100, 1000));
Equal(50, notices.Count, "updating full queue does not evict another app");
notices.TryDequeue(out ResidencyNotice? oldestRetained);
Equal(@"c:/apps/app1.exe", oldestRetained?.Observation.Process.ExecutablePath, "overflow drops oldest and duplicate retains position");
Equal(1000L, oldestRetained?.WorkingSetBytes ?? -1, "full queue update keeps latest memory");

for (int index = 2; index <= NotificationQueue.Capacity; index++)
{
    Equal(true, notices.TryDequeue(out ResidencyNotice? retained), "bounded notice FIFO retains remaining entry");
    Equal($@"C:\Apps\App{index}.exe", retained?.Observation.Process.ExecutablePath, "overflow preserves retained notice order");
}

Equal(0, notices.Count, "bounded queue drains completely");
Equal(true, notices.Enqueue(Notice(@"C:\Apps\App0.exe", 101, 500)), "evicted path is not permanently suppressed");
notices.Clear();

Equal(@"C:\Games\Steam\Steam.exe", AppTargets.ExpectedMainExecutable(
    @"C:\Games\Steam\bin\cef\cef.win64\steamwebhelper.exe"), "Steam helper resolves only to its installation's main path");
Equal(true, RulePaths.Equal(@"D:\Games\Steam\Steam.exe", AppTargets.ExpectedMainExecutable(
    @"d:/games/STEAM/bin/cef/CEF.WIN64/STEAMWEBHELPER.EXE")!), "Steam helper path recognizes case and slash aliases");
Equal(@"\\server\share\Steam\Steam.exe", AppTargets.ExpectedMainExecutable(
    @"\\server\share\Steam\bin\cef\cef.win64\steamwebhelper.exe"), "Steam helper supports UNC installation paths");
Equal(@"E:\Steam\Steam.exe", AppTargets.ExpectedMainExecutable(
    @"\\?\E:\Steam\bin\cef\cef.win64\steamwebhelper.exe"), "Steam helper normalizes extended path prefixes");
Equal(@"D:\Steam.exe", AppTargets.ExpectedMainExecutable(
    @"D:\bin\cef\cef.win64\steamwebhelper.exe"), "Steam installation can be at a drive root");
Equal(@"F:\AnotherInstall\Steam.exe", AppTargets.ExpectedMainExecutable(
    @"F:\AnotherInstall\bin\cef\cef.win64\steamwebhelper.exe"), "separate Steam installations never share a guessed main path");
foreach (string unrelated in new[]
{
    @"C:\Apps\steamwebhelper.exe",
    @"C:\Steam\bin\cef\cef.win64\notsteamwebhelper.exe",
    @"C:\Steam\bin\cef\cef.win64\steamwebhelper.exe.old",
    @"C:\Steam\bin\cef\cef.win64-other\steamwebhelper.exe",
    @"C:\Steam\bin\cef\cef.win32\steamwebhelper.exe",
    @"C:\Steam\bin\cef\cef.win64\..\steamwebhelper.exe",
    @"C:\Steam\Steam.exe",
    @"Steam\bin\cef\cef.win64\steamwebhelper.exe",
    @"\\?\GLOBALROOT\Device\Steam\bin\cef\cef.win64\steamwebhelper.exe",
    @"C:\Steam\bin\cef\cef.win64\steamwebhelper.exe:alternate",
    ""
})
{
    Equal<string?>(null, AppTargets.ExpectedMainExecutable(unrelated), "unknown or invalid helper layout is not mapped");
}

Console.WriteLine($"PASS: {passed} assertions covering close policy, identity safety, Windows paths, atomic settings, notification queue and app target layouts.");
