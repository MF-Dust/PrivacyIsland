# Zeus RPC helper design

This document records the implementation plan derived from reverse engineering the local Seewo `media_capture` files. It is intentionally design-only for now.

## Goal

PrivacyIsland should keep its current injection and shared-memory IPC path as the primary protection mechanism. A future helper may add richer diagnostics or opt-in control by using Seewo's installed x86 `media_capture_client.dll` as a compatibility layer for the private Zeus RPC protocol.

The helper should not redistribute Seewo binaries. It should load the DLL from the user's local Seewo installation.

## Why a helper

The current PrivacyIsland plugin runs in a .NET host and should not directly load a 32-bit native client DLL. The recovered Seewo client is x86 and already wraps private details:

- Zeus discovery and connection setup
- RPC request framing
- protobuf serialization and response parsing
- H264 push callback dispatch

Reimplementing raw TCP frames from strings alone would be fragile. Calling the installed client DLL through a separate x86 process is a narrower and more compatible boundary.

## Recovered client API

Observed in `SeewoCore/toolbox/rtcRemoteDesktop/media_capture_client.dll`:

- constructor: `??0MediaCaptureClient@@QAE@ABV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z`
- destructor: `??1MediaCaptureClient@@QAE@XZ`
- `Connect(std::string host, unsigned short port)`: `?Connect@MediaCaptureClient@@QAE_NABV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@G@Z`
- `Connect(bool autoFind, std::string node)`: `?Connect@MediaCaptureClient@@QAE_N_NABV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@Z`
- `Close()`: `?Close@MediaCaptureClient@@QAEXXZ`
- `IsConnect()`: `?IsConnect@MediaCaptureClient@@QAE_NXZ`
- `OpenDevice()`: `?OpenDevice@MediaCaptureClient@@QAE_NXZ`
- `CloseDevice()`: `?CloseDevice@MediaCaptureClient@@QAE_NXZ`
- `AddH264Stream(unsigned int width, unsigned int height)`: `?AddH264Stream@MediaCaptureClient@@QAE_NII@Z`
- `RemoveH264Stream(unsigned int width, unsigned int height)`: `?RemoveH264Stream@MediaCaptureClient@@QAE_NII@Z`
- `RequestH264StreamKey()`: `?RequestH264StreamKey@MediaCaptureClient@@QAE_NXZ`
- `SetDataCallback(...)`: `?SetDataCallback@MediaCaptureClient@@QAEXABV?$function@$$A6AXPBDIII_N@Z@std@@@Z`

Wrapper RVAs in the same DLL:

- `Connect(bool, string)`: `0xdf60`, dispatches to implementation around `0xaa40`
- `Connect(host, port)`: `0xdf70`, dispatches through a virtual slot
- `CaptureCameraImage`: `0xe080`, implementation around `0xab30`
- `CaptureCameraImageFile`: `0xe090`, implementation around `0xaf00`
- `OpenDevice`: `0xe0a0`, implementation around `0xb240`
- `CloseDevice`: `0xe0b0`, implementation around `0xb370`
- `AddH264Stream`: `0xe0c0`, implementation around `0xb4a0`
- `RemoveH264Stream`: `0xe0d0`, implementation around `0xb600`
- `RequestH264StreamKey`: `0xe0e0`, implementation around `0xb760`
- `SetDataCallback`: `0xe0f0`, writes a global callback slot

The device/H264 methods call a shared `MediaCaptureClientImpl::SendRequest` body around RVA `0xb9b0`. The implementation builds protobuf request objects, pushes a method name string, sends through the underlying Zeus client, and parses a protobuf `CmdResponse`.

## Safe first phase

The first helper should be read-only and diagnostic-only:

1. Accept the path to `media_capture_client.dll`.
2. Accept either a known port or a list of listening ports collected by PrivacyIsland.
3. Dynamically load the DLL in an x86 process.
4. Construct `MediaCaptureClient`.
5. Try `Connect("127.0.0.1", port)` for each candidate port.
6. Call `IsConnect()`.
7. Call `Close()`.
8. Return a small JSON result to stdout.

Suggested JSON shape:

```json
{
  "loaded": true,
  "connected": true,
  "port": 12345,
  "schema": "current",
  "error": ""
}
```

This phase must not call `OpenDevice`, `CaptureCameraImage`, or H264 stream methods.

## Optional later phases

After the read-only probe is stable:

- Add schema detection using the embedded `media_capture.proto` descriptor.
- Add an opt-in `OpenDevice` / `CloseDevice` command for diagnostics only.
- Add opt-in H264 stream monitoring with `SetDataCallback`, reporting only frame counters and key-frame presence.
- Keep frame data out of logs and PrivacyIsland UI by default.

## Integration boundary

PrivacyIsland should call the helper as a child process with a short timeout. The plugin should treat helper failure as diagnostic-only and should not disable the existing injection-based protection path.

Recommended fields in the diagnostics UI:

- Seewo client DLL found: yes/no
- helper executable found: yes/no
- Zeus RPC probe: not run / connected / failed
- selected port
- schema: legacy / current / unknown
- last helper error

## Open questions

- Whether `Connect(bool autoFind, std::string node)` works reliably with node name `SeewoMediaCapture` across installed versions.
- Whether some Seewo deployments expose the RPC server only after `media_capture.exe` has fully initialized its `RpcServer_zsystem`.
- Whether the installed client DLL requires its original directory as the process DLL search path because it depends on sibling Zeus/protobuf/runtime DLLs.
- Whether all target machines use the newer H264 schema or still ship the legacy raw image stream client.
