using System.Runtime.InteropServices;

namespace Scour.Core.Cleaning;

/// <summary>Sends a single file to the Recycle Bin via the shell, with no UI.</summary>
internal static partial class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <returns>0 on success, otherwise the shell's error code.</returns>
    public static int Send(string path)
    {
        var op = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0", // Double-null-terminated list of one.
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        var result = SHFileOperationW(ref op);
        return result != 0 ? result : op.AnyOperationsAborted ? -1 : 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
#pragma warning disable SYSLIB1054 // The struct holds strings, which LibraryImport can't marshal by ref.
    private static extern int SHFileOperationW(ref ShFileOpStruct op);
#pragma warning restore SYSLIB1054
}
