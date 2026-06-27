using System.Net;
using System.Runtime.InteropServices;

namespace PrivacyIsland.Native;

internal static class TcpTable
{
    const int AfInet = 2;
    const int TcpTableOwnerPidAll = 5;
    const uint MibTcpStateListen = 2;

    public static IReadOnlyList<int> GetListeningPorts(int pid)
    {
        if (pid <= 0) return Array.Empty<int>();

        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0) return Array.Empty<int>();

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
            if (result != 0) return Array.Empty<int>();

            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            var ports = new SortedSet<int>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                if (row.State != MibTcpStateListen || row.OwningPid != (uint)pid) continue;
                ports.Add(PortFromNetworkOrder(row.LocalPort));
            }

            return ports.ToArray();
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
