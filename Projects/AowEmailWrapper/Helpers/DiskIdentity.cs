using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// Tells whether two paths lead to the same folder or file on disk. Paths that differ as text can:
    /// through a junction or a symbolic link, a drive letter made with subst, a short (8.3) name, and
    /// under Wine, which shows a Linux home folder's Documents both as C:\users\name\Documents and
    /// under the drive letter of the Linux file system. What is compared is the volume's serial number
    /// and the file's ID on that volume, which Windows and Wine both report for an open handle.
    /// </summary>
    public static class DiskIdentity
    {
        #region Win32 API routines

        private const uint FileReadAttributes = 0x80;
        private const uint OpenExisting = 3;
        //Needed to open a folder
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const int FileIdInfoClass = 18;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo
        {
            public ulong VolumeSerialNumber;
            public ulong FileIdLow;
            public ulong FileIdHigh;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileIdInfo info, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

        #endregion

        #region Private Members

        //Folders are compared for every line of the Activity Log, so an answer is kept for a few seconds
        private const int CacheMilliseconds = 5000;
        private const int CacheLimit = 256;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CachedIdentity> FolderCache =
            new Dictionary<string, CachedIdentity>(StringComparer.OrdinalIgnoreCase);

        private struct Identity : IEquatable<Identity>
        {
            public bool Known;
            //The two calls number files differently, so an ID is only compared with one from the same call
            public bool Wide;
            public ulong Volume;
            public ulong Low;
            public ulong High;

            public bool Equals(Identity other)
            {
                return Known && other.Known && Wide == other.Wide && Volume == other.Volume && Low == other.Low && High == other.High;
            }
        }

        private struct CachedIdentity
        {
            public long ReadAt;
            public Identity Identity;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// True when both paths lead to one existing folder. False when either is missing or cannot be
        /// opened, so a caller that needs "the same text" as well compares the text first.
        /// </summary>
        public static bool SameFolder(string a, string b)
        {
            Identity first = FolderIdentity(a);
            return first.Known && first.Equals(FolderIdentity(b));
        }

        /// <summary>True when both paths lead to one existing file; read afresh on every call.</summary>
        public static bool SameFile(string a, string b)
        {
            Identity first = Read(a);
            return first.Known && first.Equals(Read(b));
        }

        #endregion

        #region Private Methods

        private static Identity FolderIdentity(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return default(Identity);
            }

            long now = Environment.TickCount64;
            lock (CacheLock)
            {
                CachedIdentity cached;
                if (FolderCache.TryGetValue(folder, out cached) && now - cached.ReadAt < CacheMilliseconds)
                {
                    return cached.Identity;
                }
            }

            Identity identity = Read(folder);
            lock (CacheLock)
            {
                if (FolderCache.Count >= CacheLimit)
                {
                    FolderCache.Clear();
                }
                FolderCache[folder] = new CachedIdentity { ReadAt = now, Identity = identity };
            }
            return identity;
        }

        private static Identity Read(string path)
        {
            Identity identity = default(Identity);
            if (string.IsNullOrWhiteSpace(path))
            {
                return identity;
            }

            try
            {
                using (SafeFileHandle handle = CreateFileW(Path.GetFullPath(path), FileReadAttributes,
                    FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero))
                {
                    if (handle.IsInvalid)
                    {
                        return identity;
                    }

                    FileIdInfo wide;
                    ByHandleFileInformation narrow;
                    if (GetFileInformationByHandleEx(handle, FileIdInfoClass, out wide, (uint)Marshal.SizeOf(typeof(FileIdInfo))))
                    {
                        identity.Known = true;
                        identity.Wide = true;
                        identity.Volume = wide.VolumeSerialNumber;
                        identity.Low = wide.FileIdLow;
                        identity.High = wide.FileIdHigh;
                    }
                    else if (GetFileInformationByHandle(handle, out narrow))
                    {
                        //File systems without the wide ID (FAT, some network shares)
                        identity.Known = true;
                        identity.Volume = narrow.VolumeSerialNumber;
                        identity.Low = ((ulong)narrow.FileIndexHigh << 32) | narrow.FileIndexLow;
                    }
                }
            }
            catch (Exception)
            {
                //A path that cannot be read has no identity; the caller falls back to comparing the text
            }
            return identity;
        }

        #endregion
    }
}
