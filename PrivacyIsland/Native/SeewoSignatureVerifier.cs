using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PrivacyIsland.Native;

internal static class SeewoSignatureVerifier
{
    const string Publisher = "Guangzhou Shirui Electronics Co., Ltd.";
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsSignedBySeewo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var file = new FileInfo(path);
            if (Cache.TryGetValue(file.FullName, out var cached) &&
                cached.Length == file.Length && cached.LastWriteUtc == file.LastWriteTimeUtc)
                return cached.Valid;

            bool valid = VerifyTrust(file.FullName) && IsSeewoPublisher(file.FullName);
            Cache[file.FullName] = new CacheEntry(file.Length, file.LastWriteTimeUtc, valid);
            return valid;
        }
        catch { return false; }
    }

    static bool IsSeewoPublisher(string path)
    {
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        return string.Equals(
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            Publisher,
            StringComparison.OrdinalIgnoreCase);
    }

    static bool VerifyTrust(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
            var data = new WinTrustData(fileInfoPtr);
            Guid action = GenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    readonly record struct CacheEntry(long Length, DateTime LastWriteUtc, bool Valid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    readonly struct WinTrustFileInfo
    {
        readonly uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] readonly string FilePath;
        readonly IntPtr FileHandle;
        readonly IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    readonly struct WinTrustData
    {
        readonly uint StructSize;
        readonly IntPtr PolicyCallbackData;
        readonly IntPtr SipClientData;
        readonly uint UiChoice;
        readonly uint RevocationChecks;
        readonly uint UnionChoice;
        readonly IntPtr FileInfo;
        readonly uint StateAction;
        readonly IntPtr StateData;
        readonly IntPtr UrlReference;
        readonly uint ProviderFlags;
        readonly uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;             // WTD_UI_NONE
            RevocationChecks = 0;     // WTD_REVOKE_NONE
            UnionChoice = 1;          // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = 0;          // WTD_STATEACTION_IGNORE
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x1010;   // no revocation check + cached URLs only
            UiContext = 0;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData trustData);
}
