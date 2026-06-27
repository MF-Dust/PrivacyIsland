namespace PrivacyIsland.Orchestrator;

/// <summary>
/// Static protocol facts recovered from Seewo media_capture binaries.
/// This is metadata only; it deliberately does not embed or redistribute Seewo code.
/// </summary>
internal static class MediaCaptureProtocol
{
    public static readonly Operation[] LegacyOperations =
    [
        new("CaptureCameraImage", "CaptureImageRequest", "CaptureImageResponse", "capture one in-memory image"),
        new("CaptureCameraImageFile", "CaptureImageFileRequest", "CaptureImageFileResponse", "capture image to a temp file"),
        new("StartCameraStream", "CaptureImageStreamRequest", "CaptureImageStreamResponse", "start raw image stream"),
        new("StopCameraStream", "streamId", "bool", "stop raw image stream"),
    ];

    public static readonly Operation[] CurrentOperations =
    [
        new("CaptureCameraImage", "CaptureImageRequest", "CaptureImageResponse", "capture one in-memory image"),
        new("CaptureCameraImageFile", "CaptureImageFileRequest", "CaptureImageFileResponse", "capture image to a temp file"),
        new("OpenDevice", "DeviceRequest", "CmdResponse", "open selected camera device"),
        new("CloseDevice", "DeviceRequest", "CmdResponse", "close selected camera device"),
        new("AddH264Stream", "H264StreamRequest", "CmdResponse", "start H264 stream"),
        new("RemoveH264Stream", "H264StreamRequest", "CmdResponse", "stop H264 stream"),
        new("H264StreamKey", "H264StreamKeyRequest", "CmdResponse", "request H264 key frame"),
        new("PostH264Stream", "H264Frame", "event", "server pushes H264 frames"),
    ];

    public static readonly Message[] CurrentMessages =
    [
        new("CaptureImageRequest", "width:uint32, height:uint32, deviceId:string, format:ImageFormat, occupy:uint32"),
        new("CaptureImageFileRequest", "width:uint32, height:uint32, deviceId:string, format:FileFormat, path:string, expiration:uint32, occupy:uint32"),
        new("CaptureImageResponse", "result:bool, data:bytes, width:uint32, height:uint32, stride:uint32, deviceId:string"),
        new("CaptureImageFileResponse", "result:bool, path:string, width:uint32, height:uint32"),
        new("DeviceRequest", "deviceId:string"),
        new("H264StreamRequest", "width:uint32, height:uint32, deviceId:string"),
        new("H264StreamKeyRequest", "deviceId:string"),
        new("CmdResponse", "result:bool"),
        new("H264Frame", "data:bytes, width:uint32, height:uint32, key:bool, deviceId:string"),
        new("CaptureDeviceLost", "deviceId:string"),
    ];

    public static string CapabilitySummary =>
        $"current={string.Join(", ", CurrentOperations.Select(o => o.Name))}; legacy={string.Join(", ", LegacyOperations.Select(o => o.Name))}";

    public sealed record Operation(string Name, string Request, string Response, string Purpose);
    public sealed record Message(string Name, string Fields);
}
