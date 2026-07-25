# AGENTS.md

## Repository scope

PrivacyIsland is a ClassIsland v2 plugin, not a standalone desktop app. It targets `net8.0-windows`, uses Avalonia/FluentAvalonia for UI, and ships the checked-in x86 `PrivacyIslandHook.dll` and `nmm_injector.exe`.

Read `CLAUDE.md` for the full architecture notes and `GUIDE.md` before changing a ClassIsland extension point. Do not apply WPF/XAML assumptions to the settings or notification code.

## Fast verification

Run these from the repository root after code changes:

```powershell
dotnet build PrivacyIsland.slnx -c Release
dotnet run --project PrivacyIsland.SmokeTest\PrivacyIsland.SmokeTest.csproj -c Release --no-build
dotnet run --project PrivacyIsland.SmokeTest\PrivacyIsland.SmokeTest.csproj -c Release --no-build -- privacy
dotnet build PrivacyIsland\PrivacyIsland.csproj -c Release -p:CreateCipx=true
```

The smoke project has no test framework. `Program.cs` dispatches `default`, `privacy`, `signature`, and `live`; category checks live in `IpcChecks.cs`, `PrivacyChecks.cs`, `SignatureChecks.cs`, and `LiveChecks.cs`. `live` starts a real 32-bit process and injects native code, so run it only as an explicit manual environment check.

## Architecture and performance rules

- `CaptureMonitor` is the hosted orchestrator. Its 1-second timer must stay cheap; `MonitoringScanner` performs the heavy process/capability scan on its 3-second cadence.
- BootConfig/TCP diagnostics are low-frequency: refresh on target identity changes or an explicit request, never on every timer tick or UI refresh.
- Keep process enumeration, signature checks, TCP reads, and file I/O off Avalonia's UI thread. `CaptureMonitor` marshals events to `Dispatcher.UIThread`; `EventDispatch` isolates subscriber exceptions.
- Keep `MonitoringScanner.BuildSnapshot` and other pure/internal seams deterministic and OS-independent so the smoke test can cover them without mocks or new packages.
- `MainSettingsPage` coordinates `ProtectionSettingsEditor`, `DiagnosticsTestPanel`, and `SettingsUi`; preserve the code-built Avalonia UI pattern.

## Native and IPC invariants

- The plugin creates `Local\LilithSharedMem`, `Local\LilithMutex`, and `Local\LilithLogEvent` before injection.
- IPC offsets are byte-exact and total 2080 bytes. Change `PrivacyIsland/Ipc/IpcProtocol.cs`, the native contract, and `PrivacyIsland.SmokeTest/IpcChecks.cs` together.
- The injector/DLL are x86 and cross-process injection normally needs an elevated ClassIsland host. Do not replace the signed-target checks with filename-only matching.
- Persist configuration/statistics under `PluginConfigFolder`, never beside the installed plugin.

## Change checklist

- Prefer existing helpers and the smallest diff; do not add a dependency or abstraction for one use.
- Preserve user-owned untracked files and unrelated worktree changes.
- Add/update the smallest smoke assertion for non-trivial logic.
- Update `README.md`/`CHANGELOG.md` when commands or project structure change.
- Before handoff, run `git diff --check`, report skipped live/manual checks, and leave commits focused.
