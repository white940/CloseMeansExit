# CloseMeansExit

A Windows tray utility that makes closing an app an explicit choice to exit.

> **Beta test release · 0.2.3-beta.1**
>
> Only the basic functionality is implemented so far. Real-app compatibility and the overall experience are still being improved. Feedback and issue reports are welcome.

**If you find it useful, please give the project a Star ⭐!**

CloseMeansExit observes windows that disappear while their process remains alive. A non-activating card in the lower-right corner shows the executable name, full path, and working-set memory, with Add to blocklist (加入黑名单) and Not now (暂不处理) buttons. A saved rule applies on the next confirmed close action: the utility waits, verifies that no interactive windows remain, and terminates that specific process instance.

The 0.2.2 baseline has passed core tests, native Windows checks and a private fixture's notification/persistence pipeline on Windows x64. Physical mouse/keyboard input and real-app compatibility still require manual acceptance testing. See GitHub Actions and the corresponding Release for the public Beta build results.

Download published Beta portable packages from the repository's **Releases** page and read the usage notes below. [Current fix notes](docs/RELEASE-0.2.3-beta.1.md)

0.2.3 retains a close intent while its verified original window alone is still processing the request. The ten-second lifetime stays fixed. Minimized or disabled windows, additional windows, save prompts and windows returning after hiding remain protected.

## Changes in 0.2.2

- Verified Steam CEF window owners resolve to their own installation's Steam.exe ancestor, using full paths, held identities, creation times and rechecked parent relationships.
- Enabled rules with disappearance-only evidence can show an explicit close-button compatibility repair action. Saving only affects later closes.
- Opt-in close regions accept client/caption results, and quick releases await in-flight identity capture while preserving the original input time.
- Steam notices expose optional child-process termination, disabled by default. This scope can include games started by Steam; root-only rules remain root-only.

Old steamwebhelper.exe rules are retained without automatically expanding their scope. Reopen and close Steam with this version, confirm Steam.exe in the notice, and explicitly choose close-button compatibility and child-process scope as appropriate. See [the validation record](docs/VALIDATION-0.2.2.md).

## Changes in 0.2.1

- Read-only residency checks now inspect same-session Windows helper descendants instead of rejecting the entire application. Their visible windows still protect the app; protected processes remain excluded from termination.
- Monitoring captures existing windows before reporting readiness and logs verification cancellations for unlisted apps.
- Corrected user-close simulation in the smoke fixture and added regression coverage for system helpers, real polling, notification actions, saved rules and subsequent-close execution. The latter close intent is injected at the coordinator, so it does not establish physical input-hook compatibility.

See [the 0.2.1 validation record](docs/VALIDATION-0.2.1.md).

## Behavior

- Newly discovered apps are never automatically terminated. An interactive card shows the `.exe` name, full path, and working-set MB without taking focus.
- Add to blocklist persists the rule for future closes. Not now adds no rule and permits another reminder after the next close. Multiple apps are queued in order.
- These prompts use a built-in WinForms card with buttons, rather than a plain system balloon notification.
- Rules match the full executable path, case-insensitively. Adding a rule affects future close actions only.
- Standard Windows close buttons and `Alt+F4` are recognized. Original input still reaches the app.
- The utility waits at least 3 seconds and requires windows to remain absent for at least 1 second.
- Another window, a save dialog, a minimized window, a changed process identity, uncertain enumeration, or a paused / disabled rule prevents termination.
- Identity is verified with PID, process start time, and executable path. Unrelated instances are not killed by executable name.
- Child-process termination is disabled by default and optional per app. Services and restarted independent instances are not chased.
- Pausing dismisses the current card and clears the pending queue. Resuming does not replay old prompts.
- Enabled blocklist rules execute eligible closes without asking again; outcomes are recorded in the local log.
- A disappearance-only observation is described as a window disappearing, not as a confirmed click on the close button.
- The four pages are Discoveries (发现记录), Blocklist (黑名单), Current windows (当前窗口), and Activity log (运行日志).
- No network access or telemetry. Settings are in `%LOCALAPPDATA%\CloseMeansExit\settings.json`; the local activity log is `%LOCALAPPDATA%\CloseMeansExit\activity.log`. Logs record app names and events, not window titles.

**Enabling a rule authorizes forced termination.** Unsaved work, downloads, synchronization, and calls can be interrupted. Test one app at a time.

This is a desktop utility, not a browser extension. It does not inject DLLs or rewrite target-app settings. It runs without automatic elevation and skips protected targets when access is denied.

## Run

Extract the `win-x64` or `win-arm64` Windows ZIP completely and run `CloseMeansExit.exe`. No separately installed .NET runtime is needed for the self-contained package.

Close an app normally. If it remains alive in the background, review its name, path, and working set on the card, then choose Add to blocklist or Not now. Adding a rule saves it immediately but never kills this newly discovered instance. Reopen and close the app again to test the rule. You can also manage apps in Discoveries and Current windows.

Use the tray menu to pause monitoring or choose Exit utility (退出工具). Closing the management window hides it to the tray and shows an explanatory balloon; background-app prompts use the separate interactive card. Windows startup is opt-in.

Windows-directory programs and this utility itself cannot be added. App updates keep matching only if the full executable path stays the same. After an update or move changes that path, select the executable again and add or adjust its rule. To remove the utility, disable startup if enabled, exit from the tray, and delete the extracted directory.

### Custom title bars

For window-disappearance notices, the card offers an unchecked advanced custom-title-bar option. If selected, it is saved together with the blocklist rule; existing advanced preferences are retained when re-enabling a rule. The portable ZIP also includes `Preview-Notification.cmd` for a synthetic, side-effect-free card preview.

Some apps draw their own close buttons. An optional per-app compatibility mode treats the upper-right **48 × 40 logical pixels**, scaled for DPI, as a possible close zone. This is a heuristic, disabled by default, and does not recognize every layout. Test close, minimize, maximize, and menu actions before relying on it.

### Limits

The default target is the process that owned the closed window. Optional child handling still verifies identities, relationships, and windows. Apps may leave work in services or unrelated processes. CloseMeansExit neither blocks automatic restarts nor guarantees removal of all background memory. Memory readings are working-set figures, not a precise measure of reclaimable physical RAM.

## Build

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run:

```powershell
dotnet run --project tests/CloseMeansExit.Tests -c Release
dotnet build src/CloseMeansExit.Windows -c Release
```

Run the UI on Windows:

```powershell
dotnet run --project src/CloseMeansExit.Windows -c Release
```

Preview only the reminder card on Windows:

```powershell
dotnet run --project src/CloseMeansExit.Windows -c Release -- --notification-demo
# Or use the extracted executable
.\CloseMeansExit.exe --notification-demo
```

The demo uses a synthetic path and working-set value to inspect positioning, focus, text, and buttons. It does not monitor or terminate real processes, and it does not save any settings.

Create a portable self-contained ZIP:

```powershell
pwsh ./scripts/publish.ps1 -Runtime win-x64
pwsh ./scripts/publish.ps1 -Runtime win-arm64
```

Output is written to `artifacts/`. Core tests run on non-Windows systems; native hooks and the UI require Windows. Cross-compilation is not an interactive desktop test. Personal builds are unsigned and may receive an unknown-publisher warning.

## Publish your own repository

The source, tests and release configuration are available under the MIT license. Do not commit settings, runtime logs or local build data; see [privacy and package contents](docs/PRIVACY.md). To publish your own repository, replace `OWNER` below with the target repository owner:

```powershell
git init -b main
git add .
git commit -m "Initial CloseMeansExit beta"
git remote add origin https://github.com/OWNER/CloseMeansExit.git
git push -u origin main
git tag v0.2.3-beta.1
git push origin v0.2.3-beta.1
```

The source ZIP excludes `.git`, so initialize a repository for its first publication. Skip initialization or commit steps already completed, and retain any existing remote. The included CI configuration runs tests and Windows builds on pushes and pull requests. A `v*` tag creates x64 / ARM64 ZIPs and `SHA256SUMS` in a GitHub Release explicitly marked **Pre-release / Beta**. An ARM64 build does not establish ARM64 device compatibility.

See [manual verification](docs/TESTING.md), the [Chinese README](README.md), and the [MIT license](LICENSE).
