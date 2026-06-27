# Seewo media_capture reverse notes

These notes record protocol facts recovered from the local Seewo binaries. They are used to guide PrivacyIsland behavior and diagnostics; PrivacyIsland does not redistribute Seewo code.

## Binaries inspected

- `SeewoCore/toolbox/media_capture/media_capture.exe`
  - x86 GUI executable.
  - File description: `媒体采集`.
  - Product: `希沃管家`.
  - Version observed locally: `1.5.4.163.9a3af50`.
- `SeewoCore/media_capture_client.dll`
  - Older x86 Zeus RPC client.
  - Supports still image capture and raw image stream.
- `SeewoCore/toolbox/rtcRemoteDesktop/media_capture_client.dll`
  - Newer x86 Zeus RPC client.
  - Adds explicit device open/close and H264 stream operations.

## RPC service shape

The binaries contain `media_capture.proto` descriptors and Zeus RPC strings. `media_capture.exe` exposes operations including:

- `CaptureCameraImage`
- `CaptureCameraImageFile`
- `OpenDevice`
- `CloseDevice`
- `AddH264Stream`
- `RemoveH264Stream`
- `H264StreamKey`
- server push topic `PostH264Stream`

The client searches for a server and falls back to `127.0.0.1`. The concrete port is discovered at runtime, so PrivacyIsland diagnostics now inspect listening TCP ports owned by the target process.

The newer client DLL exports a thin C++ wrapper over the private Zeus RPC client:

- `MediaCaptureClient::Connect(std::string host, unsigned short port)`
- `MediaCaptureClient::Connect(bool autoFind, std::string node)`
- `MediaCaptureClient::Close()`
- `MediaCaptureClient::IsConnect()`
- `MediaCaptureClient::CaptureCameraImage(...)`
- `MediaCaptureClient::CaptureCameraImageFile(...)`
- `MediaCaptureClient::OpenDevice()`
- `MediaCaptureClient::CloseDevice()`
- `MediaCaptureClient::AddH264Stream(unsigned int width, unsigned int height)`
- `MediaCaptureClient::RemoveH264Stream(unsigned int width, unsigned int height)`
- `MediaCaptureClient::RequestH264StreamKey()`
- `MediaCaptureClient::SetDataCallback(...)`

Reverse notes from the current x86 client:

- Export wrappers at RVAs `0xdf60`/`0xdf70` dispatch to `Connect` implementations.
- Capture wrappers at `0xe080` and `0xe090` dispatch to implementations at `0xab30` and `0xaf00`.
- Device/H264 wrappers dispatch to `0xb240`, `0xb370`, `0xb4a0`, `0xb600`, and `0xb760`.
- These implementation functions build protobuf request objects, push the RPC method name string (`OpenDevice`, `AddH264Stream`, etc.), then call the shared `MediaCaptureClientImpl::SendRequest` body at RVA `0xb9b0`.
- `SetDataCallback` writes a process-global callback slot used for `PostH264Stream` pushes.

This means direct TCP framing should not be guessed in PrivacyIsland. A safer future compatibility layer is a separate x86 helper process that dynamically loads the installed Seewo `media_capture_client.dll`, calls its exported client API, and reports only summarized probe/control results back to the .NET plugin.

See `docs/zeus-rpc-helper-design.md` for the proposed helper boundary. That document is design-only; no helper implementation has been added.

## Protobuf messages

The current schema was parsed from the embedded `FileDescriptorProto` in both `media_capture.exe` and the newer `media_capture_client.dll`.

- `CaptureImageRequest`: `width`, `height`, `deviceId`, `format`, `occupy`
- `CaptureImageFileRequest`: `width`, `height`, `deviceId`, `format`, `path`, `expiration`, `occupy`
- `CaptureImageResponse`: `result`, `data`, `width`, `height`, `stride`, `deviceId`
- `CaptureImageFileResponse`: `result`, `path`, `width`, `height`
- `DeviceRequest`: `deviceId`
- `H264StreamRequest`: `width`, `height`, `deviceId`
- `H264StreamKeyRequest`: `deviceId`
- `CmdResponse`: `result`
- `H264Frame`: `data`, `width`, `height`, `key`, `deviceId`
- `CaptureDeviceLost`: `deviceId`

Enums:

- `ImageFormat`: observed `IMAGE_YUV420`
- `FileFormat`: observed `FILE_BITMAP`, `FILE_JPEG`, `FILE_PNG`

The legacy client schema is kept in `docs/recovered/media_capture_legacy.proto`. Its stream model differs from the current H264 API:

- Legacy stream request/response: `CaptureImageStreamRequest`, `CaptureImageStreamResponse`
- Legacy push frame: `CaptureImageFrame` with `data`, `width`, `height`, `stride`, `deviceId`, `streamId`
- Current push frame: `H264Frame` with `data`, `width`, `height`, `key`, `deviceId`

Use `docs/recovered/extract_media_capture_proto.py` to reproduce descriptor extraction from local binaries. The complete descriptors observed locally were:

- legacy client: offset `0x963b0`, size `993`, 8 messages, 2 enums
- current rtcRemoteDesktop client: offset `0x925e0`, size `1000`, 10 messages, 2 enums
- current media_capture server: offset `0x4dabf8`, size `1000`, 10 messages, 2 enums

## Implementation boundary

The recovered RPC operation names and protobuf schema are stable enough for diagnostics and capability reporting. Direct RPC calls are intentionally not implemented in the main protection path. The next implementation step should be an opt-in helper that uses the locally installed x86 Seewo client DLL as the RPC compatibility layer.
