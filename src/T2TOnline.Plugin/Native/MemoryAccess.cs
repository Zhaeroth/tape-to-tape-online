using System.Runtime.InteropServices;

namespace T2TOnline.Plugin.Native;

/// <summary>Checks on raw memory before the mod reads or writes it, so a bad pointer is refused instead of crashing the game.</summary>
internal static class MemoryAccess
{
    private const uint MemCommit = 0x1000;
    private const uint PageReadOnly = 0x02;
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;

    /// <summary>True when every byte of [<paramref name="address"/>, +<paramref name="length"/>) is committed and writable.</summary>
    public static bool IsWritable(nint address, int length) => Check(address, length, write: true);

    /// <summary>True when every byte of [<paramref name="address"/>, +<paramref name="length"/>) is committed and readable.</summary>
    public static bool IsReadable(nint address, int length) => Check(address, length, write: false);

    private static bool Check(nint address, int length, bool write)
    {
        if (address == 0 || length <= 0)
        {
            return false;
        }

        nint end = address + length;
        nint cursor = address;
        while (cursor < end)
        {
            if (VirtualQuery(cursor, out MemoryBasicInformation info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                return false;
            }

            // A guard page carries an extra flag bit, so it never matches these and is refused too.
            bool writable = info.Protect is PageReadWrite or PageWriteCopy or PageExecuteReadWrite or PageExecuteWriteCopy;
            bool allowed = writable || (!write && info.Protect is PageReadOnly or PageExecuteRead);
            if (info.State != MemCommit || !allowed)
            {
                return false;
            }

            cursor = info.BaseAddress + (nint)info.RegionSize;
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
