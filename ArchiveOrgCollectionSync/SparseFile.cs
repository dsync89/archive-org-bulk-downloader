namespace ArchiveOrgCollectionSync
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;

    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// Marks partial downloads as NTFS sparse files. Without it, writing a segment far into a preallocated file makes
    /// Windows zero-fill everything before that offset first: minutes for a multi-GB file on a hard disk or network
    /// share, inside one write that can't be cancelled (so Pause, Stop and even killing the process hang).
    /// </summary>
    public static class SparseFile
    {
        private const uint FsctlSetSparse = 0x000900C4;

        /// <summary>Creates the file if needed and marks it sparse.</summary>
        /// <returns>False when the volume doesn't support sparse files (FAT32, exFAT, some network shares).</returns>
        public static bool TrySet(string path)
        {
            return SparseFile.TrySet(path, true);
        }

        /// <summary>Clears the sparse flag once the file is completely written (best effort).</summary>
        public static void TryClear(string path)
        {
            SparseFile.TrySet(path, false);
        }

        private static bool TrySet(string path, bool sparse)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                // A synchronous handle of its own: the download stream is opened for overlapped IO.
                using SafeFileHandle handle = System.IO.File.OpenHandle(path, sparse ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                byte flag = sparse ? (byte)1 : (byte)0;
                return SparseFile.DeviceIoControl(handle, FsctlSetSparse, ref flag, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            ref byte inBuffer,
            int inBufferSize,
            IntPtr outBuffer,
            int outBufferSize,
            out int bytesReturned,
            IntPtr overlapped);
    }
}
