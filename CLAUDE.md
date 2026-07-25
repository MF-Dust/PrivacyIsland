# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PrivacyIsland is a **ClassIsland v2 plugin** (not a standalone app). It is loaded into the ClassIsland host process, detects Seewo's `media_capture.exe` accessing the camera, injects a native hook DLL that forces a random capture delay, and surfaces ClassIsland notifications / automation / lesson-linkage around those camera events. Target framework `net8.0-windows`; plugin API version `2.0.0.0`.

`README.md` (usage/install) and `GUIDE.md` (a ClassIsland plugin-dev cheat sheet distilled from the official docs + verified against this source) are the authoritative background — read `GUIDE.md` before touching any ClassIsland extension point (notification providers, triggers, actions, rules, settings pages), since ClassIsland 2.x UI is **Avalonia/FluentAvalonia, not WPF** (ignore WPF/`pack://` guidance in the English docs; use `avares://`).

## Commands

```powershell
# Build (both projects)
dotnet build PrivacyIsland.slnx -c Release

# Build AND package the plugin into a .cipx (output: PrivacyIsland/cipx/PrivacyIsland.cipx)
dotnet build PrivacyIsland\PrivacyIsland.csproj -c Release -p:CreateCipx=true

# Smoke test — IPC round-trip + privacy/refactoring regressions (fast, deterministic)
dotnet run --project PrivacyIsland.SmokeTest\PrivacyIsland.SmokeTest.csproj

# Smoke test — privacy/refactoring checks only (no shared memory or injection)
dotnet run --project PrivacyIsland.SmokeTest\PrivacyIsland.SmokeTest.csproj -- privacy

# Signature checks — pass one or more Seewo executable paths
dotnet run --project PrivacyIsland.SmokeTest\PrivacyIsland.SmokeTest.csproj -- signature <path...>

# Live injection — injects the hook DLL into 32-bit notepad (manual environment check only)
dotnet run --project PrivacyIsland.SmokeTest\PrivacyIsland.SmokeTest.csproj -- live
```

`CreateCipx` / `GenerateHashSummary` are MSBuild properties provided by the `ClassIsland.PluginSdk` targets. There is **no unit-test framework or single-test filter**. `PrivacyIsland.SmokeTest/Program.cs` only dispatches the four compatible modes; checks live in `SmokeAssert.cs`, `IpcChecks.cs`, `PrivacyChecks.cs`, `LiveChecks.cs`, and `SignatureChecks.cs`. Modes assert-and-throw (non-zero exit on failure) and print `PASS`, `PRIVACY PASS`, `LIVE PASS`, or `SIGNATURE PASS`. CI (`.github/workflows/release.yml`) builds, runs the default smoke test, packages the cipx, and publishes a Release on `v*` tags.

## Architecture — the big picture

The load-bearing, non-obvious design decisions (each spans multiple files):

**1. `PrivacyIslandRuntime` is a static hub, and it exists for a reason.** ClassIsland instantiates notification providers, triggers, rules, and actions itself via DI, on demand — those instances cannot reach the plugin's own service singletons (`CaptureMonitor`, `LessonAwareController`). So every extension object talks to the running orchestrator through the static `PrivacyIslandRuntime` (`PrivacyIslandRuntime.cs`): it holds `Monitor`/`LessonController` references (set by those services at startup), exposes static events (`StateReceived`, `ProtectionPauseChanged`) that DI-created triggers/providers subscribe to, and exposes static control methods (`Pause`, `SetDelay`, `InjectNow`, …) that actions call. When adding a new trigger/action/rule, route through this hub — do not try to inject the services.

**2. Two hosted services, registered in `Plugin.cs` `Initialize`:**
- `CaptureMonitor` (`Orchestrator/CaptureMonitor.cs`) — the orchestrator (replaces the original native `main.c`). Polls every 1s for `media_capture.exe`, and on a new PID runs `nmm_injector.exe --inject <pid> <dllpath>` to inject `PrivacyIslandHook.dll`. Failed injections retry every 15s. It also owns config/stats and the IPC bridge, and dispatches DLL state frames.
- `LessonAwareController` (`Orchestrator/LessonAwareController.cs`) — subscribes to ClassIsland's `ILessonsService` (`OnClass`/`OnBreakingTime`/`OnAfterSchool`) to auto-pause or apply a stronger delay during class. `ILessonsService` is injected as **nullable**: if the host doesn't provide it, the controller degrades to a no-op instead of failing plugin load. Default config is all-off (no interference).

The monitor delegates focused work to three small collaborators: `MonitoringScanner` samples process/capability state and builds an immutable snapshot, `HookInjector` owns native injection/ejection, and `PrivacyRiskCoordinator` owns risk state, prompts, and safe termination. The expensive scanner runs on the background timer at the 3-second heavy-poll cadence; BootConfig/TCP diagnostics refresh only when the target identity changes or an explicit request is made. `MonitoringScanner.BuildSnapshot` and the diagnostics gate are pure/internal seams covered by the smoke test.

**3. Native injection requires x86.** `PrivacyIslandHook.dll` and `nmm_injector.exe` live checked-in under `PrivacyIsland/Native/` and are copied to output + bundled into the cipx (see `.csproj`). The injector must be **x86** because `media_capture.exe` is a 32-bit process. Cross-process injection generally requires **ClassIsland running as Administrator** (OpenProcess otherwise fails) — most "injection failed" reports trace to this.

**4. IPC contract must stay byte-exact with the native side.** `Ipc/SharedMemoryBridge.cs` + `Ipc/IpcProtocol.cs` implement the host side of a shared-memory protocol (`Local\LilithSharedMem` MMF + `Local\LilithMutex` + `Local\LilithLogEvent`) that mirrors the native `struct log_data` in `shared_defs.h`. It uses **fixed byte offsets, not `[StructLayout]` marshalling** (to avoid the embedded `wchar_t[1024]` marshalling trap). Total size is asserted to be 2080 by `IpcProtocol.SelfCheck()`. **Any field change must be made in three places in lockstep: `IpcProtocol.cs`, the native `shared_defs.h`, and the hard-coded offset constants in `PrivacyIsland.SmokeTest/IpcChecks.cs`.** Also note the **plugin is the creator** of the IPC objects — `SharedMemoryBridge.Start()` must be called *before* the DLL is injected (the DLL does `OpenFileMapping`). `EventDispatch` isolates subscriber failures so one extension cannot stop the IPC reader.

**5. Threading: the IPC reader is a background thread; ClassIsland UI is Avalonia.** `CaptureMonitor.OnState` marshals state dispatch onto the Avalonia UI thread (`Dispatcher.UIThread`) before raising `PrivacyIslandRuntime.RaiseState`, because subscribers (notification provider, triggers) touch UI. Keep that boundary when adding subscribers.

**6. Layered pause + delay-override model.** Pause is not a boolean — `CaptureMonitor.SetPauseSource(key, active)` tracks a set of sources (`manual` = settings page, `automation` = actions, `lesson` = lesson linkage); effective pause = *any* source active. Separately, a **delay override** (`ApplyDelayOverride`) is a transient min/max that is **not persisted** to `config.json` (used e.g. for stronger in-class delay), taking precedence over the persisted base delay. `ApplyEffectiveToBridge()` collapses (override-or-base delay + effective pause + stealth) into one `WriteConfig` to shared memory.

**7. `Simulate` is the test seam.** `CaptureMonitor.Simulate` / `SharedMemoryBridge.Simulate` write a synthetic frame through the *real* reader thread + dispatch path, so the entire ClassIsland-side chain (CameraActive, stats, notifications, triggers, rules) can be verified **without real injection or admin**. This backs both the settings page's 功能测试 (function-test) section and the smoke test.

**8. Settings and smoke code are intentionally split by responsibility.** `MainSettingsPage` is a small coordinator; `ProtectionSettingsEditor` owns configuration controls, `DiagnosticsTestPanel` owns diagnostics/simulation, and `SettingsUi` contains only shared in-code Avalonia builders. Keep the UI on Avalonia's UI thread and keep slow process/TCP/file work out of it. The smoke test is similarly split into one file per check category; add the smallest assert that protects a non-trivial change instead of introducing a test framework.

## Conventions and constraints

- **Config lives in `PluginConfigFolder`** (`config.json`, stats), never the install directory — ClassIsland wipes the install dir on update. `CaptureMonitor` receives `PluginConfigFolder` from `Plugin.Initialize`.
- **Notification settings migrated out of `PluginConfig`.** Text/color/duration/speech now live on `CameraNotificationSettings` (the notification provider's own settings); `CameraNotificationProvider.TryMigrateLegacyConfig` one-time-migrates them from the legacy `PluginConfig` fields. Notification tuning belongs in ClassIsland's notification-provider settings UI, not the plugin's main settings page.
- **Delays are clamped to 1–30s, max ≥ min** (`PluginConfig.Clamp`), matching the original program.
- **Plugin logs go to the ClassIsland host log** (`Logging/PluginLog.cs`), not a separate file/dir.
- The settings page (`Settings/MainSettingsPage.cs`) builds its Avalonia UI **in code** (no XAML), using FluentAvalonia controls.
- `docs/` holds reverse-engineering notes on the `media_capture.exe` target (recovered protobuf `.proto` files, RPC helper design) — reference material for understanding the injected target, not build inputs.
- Keep heavy OS/process/TCP/BootConfig work on the monitor's background scan; do not add per-frame or UI-thread I/O to fix a symptom such as mouse stutter.
- Update `CHANGELOG.md` when a refactor changes commands, architecture, or validation behavior.
