using System.Net;
using System.Runtime.InteropServices;

namespace PrivacyIsland.Native;

internal static class TcpTable
{
    const int AfInet = 2;
    const int TcpTableOwnerPidAll = 5;
    const uint MibTcpStateListen = 2;
    const uint MibTcpStateEstab = 5;

    public static IReadOnlyList<int> GetListeningPorts(int pid)
    {
        if (pid <= 0) return Array.Empty<int>();

        var ports = new SortedSet<int>();
        EnumerateRows(row =>
        {
            if (row.State == MibTcpStateListen && row.OwningPid == (uint)pid)
                ports.Add(PortFromNetworkOrder(row.LocalPort));
        });
        return ports.ToArray();
    }

    /// <summary>目标进程当前的 ESTABLISHED 连接数。media_capture 是 RPC 服务器，活跃客户端连接是又一条 hook 无关的印证。</summary>
    public static int CountEstablished(int pid)
    {
        if (pid <= 0) return 0;

        int count = 0;
        EnumerateRows(row =>
        {
            if (row.State == MibTcpStateEstab && row.OwningPid == (uint)pid) count++;
        });
        return count;
    }

    static void EnumerateRows(Action<MibTcpRowOwnerPid> onRow)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0) != 0) return;

            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            for (int i = 0; i < count; i++)
                onRow(Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static int PortFromNetworkOrder(uint rawPort)
    {
        ushort network = (ushort)(rawPort & 0xFFFF);
        return (ushort)IPAddress.NetworkToHostOrder((short)network);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        uint reserved);
}
