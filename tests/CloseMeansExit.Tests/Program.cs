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

CloseWindowSource sourceWindow = new((nint)456, process);
ProcessSnapshot originalVisible = visible with { OnlySourceWindowVisible = true };
CloseSession NewSourceSession(AppRule? captured = null,
    CloseEvidence evidence = CloseEvidence.StandardCloseButton) =>
    new(new CloseObservation(process, "Example", evidence, origin) { SourceWindow = sourceWindow }, captured);

// A slow close may leave its original window visible past the grace period.
// Keep the click only while the native probe confirms that exact window alone.
foreach (CloseEvidence evidence in new[]
{
    CloseEvidence.StandardCloseButton, CloseEvidence.AltF4, CloseEvidence.CustomTitleBarZone
})
{
    AppRule delayedRule = evidence == CloseEvidence.CustomTitleBarZone ? customRule : rule;
    CloseSession delayed = NewSourceSession(delayedRule, evidence);
    Equal(DecisionKind.Wait, Advance(delayed, originalVisible, 0, delayedRule).Kind,
        evidence + " original window initially remains open");
    Equal(DecisionKind.Wait, Advance(delayed, originalVisible, 3, delayedRule).Kind,
        evidence + " original window can process close beyond grace");
    Equal(DecisionKind.Wait, Advance(delayed, originalVisible, 4, delayedRule).Kind,
        evidence + " slow close retains original intent");
    Equal(DecisionKind.Wait, Advance(delayed, hidden, 5, delayedRule).Kind,
        evidence + " delayed hide starts a new stability period");
    Equal(DecisionKind.Wait, Advance(delayed, hidden, 5.99, delayedRule).Kind,
        evidence + " delayed hide still requires one stable second");
    Equal(DecisionKind.Terminate, Advance(delayed, hidden, 6, delayedRule).Kind,
        evidence + " stable delayed hide can end the captured process");
    Equal(DecisionKind.Ignore, Advance(delayed, hidden, 7, delayedRule).Kind,
        evidence + " delayed termination is emitted only once");
}

CloseSession missingSource = NewSession(rule);
Advance(missingSource, originalVisible, 0, rule);
Equal(DecisionKind.Ignore, Advance(missingSource, originalVisible, 3, rule).Kind,
    "a source-only flag without captured window identity cannot extend waiting");

CloseSession weakSource = NewSourceSession(rule, CloseEvidence.WindowDisappeared);
Advance(weakSource, originalVisible, 0, rule);
Equal(DecisionKind.Ignore, Advance(weakSource, originalVisible, 3, rule).Kind,
    "weak disappearance evidence cannot retain an original-window close intent");
Equal(DecisionKind.Ignore, Advance(weakSource, hidden, 6, rule).Kind,
    "weak canceled source attempt cannot become executable after hiding");

CloseSession anotherWindow = NewSourceSession(rule);
Advance(anotherWindow, originalVisible, 3, rule);
Equal(DecisionKind.Ignore, Advance(anotherWindow, visible, 4, rule).Kind,
    "another, minimized, disabled or unverified visible window ends extended waiting");
Equal(DecisionKind.Ignore, Advance(anotherWindow, hidden, 6, rule).Kind,
    "removing a protected window cannot revive the old click");

CloseSession sourceReturns = NewSourceSession(rule);
Advance(sourceReturns, originalVisible, 3, rule);
Advance(sourceReturns, hidden, 4, rule);
Equal(DecisionKind.Ignore, Advance(sourceReturns, originalVisible, 4.5, rule).Kind,
    "even the original window returning after hiding permanently cancels");
Equal(DecisionKind.Ignore, Advance(sourceReturns, hidden, 6, rule).Kind,
    "rehiding a returned source window cannot reactivate the attempt");

CloseSession originalNeverCloses = NewSourceSession(rule);
Equal(DecisionKind.Wait, Advance(originalNeverCloses, originalVisible, 3, rule).Kind,
    "source window may remain visible after the grace boundary");
Equal(DecisionKind.Wait, Advance(originalNeverCloses, originalVisible, 9.99, rule).Kind,
    "repeated source checks may wait within the original click lifetime");
Equal(DecisionKind.Ignore, Advance(originalNeverCloses, originalVisible, 10, rule).Kind,
    "source checks never renew the ten-second click lifetime");
Equal(DecisionKind.Ignore, Advance(originalNeverCloses, hidden, 11, rule).Kind,
    "a source disappearing after expiry cannot revive the attempt");

CloseSession sourceHidesTooLate = NewSourceSession(rule);
Advance(sourceHidesTooLate, originalVisible, 8.9, rule);
Equal(DecisionKind.Wait, Advance(sourceHidesTooLate, hidden, 9.5, rule).Kind,
    "late disappearance still requires hidden stability");
Equal(DecisionKind.Ignore, Advance(sourceHidesTooLate, hidden, 10, rule).Kind,
    "lifetime expiry takes precedence over completing delayed stability");

CloseSession sourcePaused = NewSourceSession(rule);
Advance(sourcePaused, originalVisible, 3, rule);
Equal(DecisionKind.Ignore, Advance(sourcePaused, originalVisible, 4, rule, paused: true).Kind,
    "pausing during an extended wait cancels the click");
Equal(DecisionKind.Ignore, Advance(sourcePaused, hidden, 6, rule).Kind,
    "resuming cannot reactivate an extended wait");

foreach ((AppRule? replacement, string description) in new (AppRule?, string)[]
{
    (null, "removing the rule"),
    (rule with { Enabled = false }, "disabling the rule"),
    (rule with { IncludeChildren = true }, "changing process-tree scope"),
    (rule with { UseCustomCloseZone = true }, "changing custom-close permission")
})
{
    CloseSession changedDuringWait = NewSourceSession(rule);
    Advance(changedDuringWait, originalVisible, 3, rule);
    Equal(DecisionKind.Ignore, Advance(changedDuringWait, originalVisible, 4, replacement).Kind,
        description + " cancels an extended source wait");
    Equal(DecisionKind.Ignore, Advance(changedDuringWait, hidden, 6, rule).Kind,
        description + " cannot be undone to reuse the old click");
}

foreach ((ProcessSnapshot snapshot, string description) in new[]
{
    (originalVisible with { IsAlive = false }, "natural exit"),
    (originalVisible with { IdentityMatches = false }, "changed process identity"),
    (originalVisible with { Reliable = false }, "unreliable window probe")
})
{
    CloseSession invalidDuringWait = NewSourceSession(rule);
    Advance(invalidDuringWait, originalVisible, 3, rule);
    Equal(DecisionKind.Ignore, Advance(invalidDuringWait, snapshot, 4, rule).Kind,
        description + " cancels an extended source wait");
    Equal(DecisionKind.Ignore, Advance(invalidDuringWait, hidden, 6, rule).Kind,
        description + " cannot reactivate after a later valid probe");
}

CloseSession sourceUnlistedAtClick = NewSourceSession();
Advance(sourceUnlistedAtClick, originalVisible, 3, rule);
Advance(sourceUnlistedAtClick, hidden, 5, rule);
Equal(DecisionKind.Candidate, Advance(sourceUnlistedAtClick, hidden, 6, rule).Kind,
    "adding a rule during a slow close still cannot arm the past click");

CloseSession NewBackgroundSession(AppRule? captured = null) =>
    NewSession(captured, CloseEvidence.BlacklistedBackground);

CloseSession background = NewBackgroundSession(rule);
Equal(DecisionKind.Wait, Advance(background, hidden, 0, rule).Kind,
    "enabled blacklist starts a fresh background observation without input or custom-close permission");
Equal(DecisionKind.Wait, Advance(background, hidden, 2.99, rule).Kind,
    "automatic background cleanup still waits three seconds");
Equal(DecisionKind.Terminate, Advance(background, hidden, 3, rule).Kind,
    "a listed hidden process is ended without a close-button signal");
Equal(DecisionKind.Ignore, Advance(background, hidden, 4, rule).Kind,
    "one background observation can only emit termination once");

foreach (AppRule? captured in new AppRule?[]
{
    null, rule with { Enabled = false }, rule with { ExecutablePath = @"D:\Other\Example.exe" }
})
{
    CloseSession unauthorizedBackground = NewBackgroundSession(captured);
    Equal(DecisionKind.Ignore, Advance(unauthorizedBackground, hidden, 0, rule).Kind,
        "background cleanup requires an enabled exact-path rule at observation time");
    Equal(DecisionKind.Ignore, Advance(unauthorizedBackground, hidden, 4, rule).Kind,
        "a later valid rule cannot revive an unauthorized background observation");
}

CloseSession backgroundWindow = NewBackgroundSession(rule);
Equal(DecisionKind.Ignore, Advance(backgroundWindow, originalVisible, 0, rule).Kind,
    "any visible window immediately cancels background cleanup even with the original-window flag");
Equal(DecisionKind.Ignore, Advance(backgroundWindow, hidden, 3, rule).Kind,
    "a canceled background observation cannot be replayed when the window later hides");
CloseSession backgroundReturned = NewBackgroundSession(rule);
Advance(backgroundReturned, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(backgroundReturned, visible, 1, rule).Kind,
    "a window appearing during automatic cleanup cancels the pending attempt");
Equal(DecisionKind.Ignore, Advance(backgroundReturned, hidden, 3, rule).Kind,
    "hiding a returned window requires a new background observation");

CloseSession backgroundLateHide = NewBackgroundSession(rule);
Equal(DecisionKind.Wait, Advance(backgroundLateHide, hidden, 2.9, rule).Kind,
    "automatic cleanup measures hidden stability from the first reliable hidden probe");
Equal(DecisionKind.Wait, Advance(backgroundLateHide, hidden, 3, rule).Kind,
    "the three-second grace alone cannot bypass hidden stability");
Equal(DecisionKind.Terminate, Advance(backgroundLateHide, hidden, 3.9, rule).Kind,
    "automatic cleanup requires both grace and a full stable hidden second");

foreach ((AppRule? replacement, string description) in new (AppRule?, string)[]
{
    (null, "removed rule"),
    (rule with { Enabled = false }, "disabled rule"),
    (rule with { IncludeChildren = true }, "changed termination scope"),
    (rule with { UseCustomCloseZone = true }, "changed rule settings")
})
{
    CloseSession changedBackground = NewBackgroundSession(rule);
    Advance(changedBackground, hidden, 0, rule);
    Equal(DecisionKind.Ignore, Advance(changedBackground, hidden, 1, replacement).Kind,
        description + " cancels pending automatic cleanup");
    Equal(DecisionKind.Ignore, Advance(changedBackground, hidden, 3, rule).Kind,
        description + " cannot be undone to reuse the canceled background attempt");
}

foreach ((ProcessSnapshot snapshot, string description) in new[]
{
    (hidden with { IsAlive = false }, "natural exit"),
    (hidden with { IdentityMatches = false }, "reused PID or changed identity"),
    (hidden with { Reliable = false }, "unreliable process/window probe")
})
{
    CloseSession invalidBackground = NewBackgroundSession(rule);
    Advance(invalidBackground, hidden, 0, rule);
    Equal(DecisionKind.Ignore, Advance(invalidBackground, snapshot, 1, rule).Kind,
        description + " cancels automatic cleanup");
    Equal(DecisionKind.Ignore, Advance(invalidBackground, hidden, 3, rule).Kind,
        description + " cannot recover the canceled automatic cleanup attempt");
}

CloseSession pausedBackground = NewBackgroundSession(rule);
Advance(pausedBackground, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(pausedBackground, hidden, 1, rule, paused: true).Kind,
    "pausing cancels automatic cleanup already in its grace period");
Equal(DecisionKind.Ignore, Advance(pausedBackground, hidden, 3, rule).Kind,
    "resuming must create a fresh background observation");
CloseSession expiredBackground = NewBackgroundSession(rule);
Advance(expiredBackground, hidden, 0, rule);
Equal(DecisionKind.Ignore, Advance(expiredBackground, hidden, 10, rule).Kind,
    "automatic cleanup never uses an expired process observation");
CloseSession rollbackBackground = NewBackgroundSession(rule);
Advance(rollbackBackground, hidden, 1, rule);
Equal(DecisionKind.Ignore, Advance(rollbackBackground, hidden, .5, rule).Kind,
    "clock rollback cancels automatic cleanup");

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
