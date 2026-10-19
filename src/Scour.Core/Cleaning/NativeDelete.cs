using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scour.Core.Cleaning;

/// <summary>
/// Deletes a file through an open handle so the checks and the delete see the same file.
/// Opening with <c>FILE_FLAG_OPEN_REPARSE_POINT</c> and checking the handle's final path means a
/// folder swapped for a junction between scan and clean can't redirect the delete.
/// </summary>
internal static partial class NativeDelete
{
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileWriteAttributes = 0x0100;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;

    private const int FileBasicInfoClass = 0;
    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;

    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadOnly = 0x10;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;

    public readonly record struct FileState(long Size, DateTime LastWriteUtc, FileAttributes Attributes, string FinalPath);

    /// <summary>Opens a file for deletion without following reparse points.</summary>
    public static SafeFileHandle Open(string path, out int error)
    {
        var handle = CreateFileW(@"\\?\" + path, Delete | FileReadAttributes | FileWriteAttributes, ShareAll,
            IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    public static FileState Inspect(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var lastWrite = DateTime.FromFileTimeUtc(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow);
        return new FileState(size, lastWrite, (FileAttributes)info.FileAttributes, GetFinalPath(handle));
    }

    /// <summary>Marks the open file for deletion; it disappears when the handle closes.</summary>
    public static int MarkForDeletion(SafeFileHandle handle)
    {
        // POSIX semantics removes the name immediately even if another process holds it open
        // with FILE_SHARE_DELETE. Not supported on FAT, so fall back to the classic disposition.
        var ex = new FileDispositionInfoEx { Flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly };
        if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref ex, (uint)Marshal.SizeOf<FileDispositionInfoEx>()))
            return 0;

        ClearReadOnly(handle);
        var classic = new FileDispositionInfo { DeleteFile = 1 };
        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref classic, (uint)Marshal.SizeOf<FileDispositionInfo>())
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    private static void ClearReadOnly(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) || (info.FileAttributes & (uint)FileAttributes.ReadOnly) == 0)
            return;
        var basic = new FileBasicInfo { FileAttributes = (info.FileAttributes & ~(uint)FileAttributes.ReadOnly) | (uint)FileAttributes.Normal };
        SetFileInformationByHandle(handle, FileBasicInfoClass, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>());
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        uint length;
        unsafe
        {
            fixed (char* p = buffer)
                length = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
        }
        if (length == 0 || length > buffer.Length)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new string(buffer, 0, (int)length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        // FILETIMEs are pairs of DWORDs (4-byte aligned), so they can't be declared as long here.
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle handle, char* path, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDispositionInfoEx info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDispositionInfo info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileBasicInfo info, uint size);
}
