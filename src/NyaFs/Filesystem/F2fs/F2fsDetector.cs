using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.F2fs
{
    /// <summary>
    /// F2FS detection: the superblock magic lives at offset 1024.
    /// </summary>
    public static class F2fsDetector
    {
        public const uint MAGIC = 0xF2F52010;

        public static bool IsF2fs(byte[] Raw, long Offset = 0) =>
            (Raw.Length - Offset >= 1028) && (Raw.ReadUInt32(Offset + 1024) == MAGIC);
    }
}
