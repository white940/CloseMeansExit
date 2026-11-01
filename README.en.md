# CloseMeansExit

A Windows tray utility that automatically ends background instances of applications you add to its blacklist.

> **Beta · 0.3.0-beta.1** — Only basic functionality is implemented. Compatibility and usability are still being improved.

**If you find it useful, please give the project a Star ⭐!**

[Downloads](https://github.com/white940/CloseMeansExit/releases) · [Release notes](docs/RELEASE-0.3.0-beta.1.md) · [中文](README.md)

## Behavior

An enabled rule authorizes automatic processing of running instances at its exact executable path. Once the application and its descendants have no visible windows, the tool waits briefly, checks identity again and ends the matching instance. A close click, Alt+F4 or custom title-bar compatibility is no longer required.

This includes instances already hidden when a rule is added or monitoring starts. Resuming monitoring and re-enabling a rule trigger fresh checks. Unlisted applications remain discovery candidates requiring your choice.

- Visible, minimized and save-dialog windows protect the application, including windows in descendants.
- Reappearing windows cancel the current attempt. A later return to the background can start a fresh check.
- Pausing or changing/removing a rule cancels pending operations.
- PID, creation time, executable path and windows are revalidated before termination. Uncertain results cancel the attempt.
- Each attempt waits at least about three seconds and needs one second of continuous hiding. Polling adds a small delay; failures back off before retrying.
- New instances are checked independently against current rules. The utility does not alter services or application startup settings.

## Use and upgrade

Extract the portable ZIP and run `CloseMeansExit.exe`. Choose `win-x64` for common Intel/AMD computers or `win-arm64` for Windows ARM devices. The packages are self-contained.

Add an application through its residency notification or the blacklist tab. Existing background instances can be ended after saving the rule. Pause monitoring or disable a rule when background downloads, synchronization or other work should continue.

**Existing enabled rules adopt this behavior when upgrading.** Forced termination can interrupt work or discard unsaved data.

Use the main `Steam.exe` rule for Steam. If only the main process should end, disable any old `steamwebhelper.exe` rule too. Including descendants is an explicit, default-off option and can include games launched through Steam. Descendant windows protect the application even with root-only termination.

Closing the manager hides it to the tray. Use Exit Tool to stop monitoring. Startup with Windows is optional. `Preview-Notification.cmd` displays synthetic UI only.

## Build and privacy

Use .NET 10 SDK; Windows smoke checks require Windows:

```powershell
dotnet run --project tests/CloseMeansExit.Tests -c Release
dotnet build src/CloseMeansExit.Windows -c Release
dotnet run --project src/CloseMeansExit.Windows -c Release --no-build -- --smoke-test
./scripts/publish.ps1 -Runtime win-x64
```

Packages are written to `artifacts`. CI builds x64 and ARM64; version tags publish Beta prereleases and checksums. Automated checks use disposable fixtures, not actual user applications. ARM64 device compatibility requires separate testing.

Settings and logs stay in `%LOCALAPPDATA%\CloseMeansExit`. The application does not connect to the network or collect telemetry. Never commit local settings or logs. Windows system paths, the utility itself and other sessions are protected; access failures do not trigger elevation.

See [testing](docs/TESTING.md), [privacy](docs/PRIVACY.md) and [MIT License](LICENSE). Historical validation documents describe their corresponding older versions.
