# PrivacyIsland

PrivacyIsland is a ClassIsland v2 plugin that protects against Seewo camera access by bundling the NoMoreMonitor native hook payload and exposing the workflow through ClassIsland notifications, automation, diagnostics, and settings.

## Projects

- `PrivacyIsland/` - ClassIsland plugin (`net8.0-windows`).
- `PrivacyIsland.SmokeTest/` - IPC smoke test for the shared-memory bridge.
- `PrivacyIsland/Native/` - bundled x86 native hook DLL and injector helper used by the plugin package.

## Build

```powershell
dotnet build PrivacyIsland\PrivacyIsland.csproj -c Release -p:CreateCipx=true
```

The plugin package is written to:

```text
PrivacyIsland/cipx/PrivacyIsland.cipx
```

## Test

```powershell
dotnet run --project PrivacyIsland.SmokeTest\PrivacyIsland.SmokeTest.csproj
```
