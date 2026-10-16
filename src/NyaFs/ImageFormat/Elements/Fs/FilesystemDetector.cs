using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Elements.Fs
{
    public static class FilesystemDetector
    {
        const uint COMPAT_HAS_JOURNAL = 0x4;
        const uint EXT4_INCOMPAT = 0x1F7C0;
        const uint EXT4_RO_COMPAT = 0x2778;

        private static bool IsExt(byte[] Raw) =>
            (Raw.Length >= 0x43A) && (Raw.ReadUInt16(0x438) == 0xEF53);

        private static bool HasJournal(byte[] Raw) =>
            IsExt(Raw) && ((Raw.ReadUInt32(0x45C) & COMPAT_HAS_JOURNAL) != 0);

        private static bool HasExt4Features(byte[] Raw) =>
            IsExt(Raw) &&
            (((Raw.ReadUInt32(0x460) & EXT4_INCOMPAT) != 0) || ((Raw.ReadUInt32(0x464) & EXT4_RO_COMPAT) != 0));

        public static Types.FsType DetectFs(byte[] Raw)
        {
            var Magic = Raw.ReadUInt32BE(0);
            if (Magic == 0x30373037u) // CPIO start of block
                return Types.FsType.Cpio;

            if (Magic == 0x68737173) // hsqs magic
                return Types.FsType.SquashFs;

            if (Magic == 0x453dcd28u) // cramfs magic
                return Types.FsType.CramFs;

            if (Magic == 0x2d726f6du) // romfs magic
                return Types.FsType.RomFs;

            if (IsExt(Raw))
            {
                if (HasExt4Features(Raw))
                    return Types.FsType.Ext4;
                if (HasJournal(Raw))
                    return Types.FsType.Ext3;
                return Types.FsType.Ext2;
            }

            // tar: ustar magic at offset 257
            if ((Raw.Length >= 263) &&
                (Raw[257] == (byte)'u') && (Raw[258] == (byte)'s') && (Raw[259] == (byte)'t') &&
                (Raw[260] == (byte)'a') && (Raw[261] == (byte)'r'))
                return Types.FsType.Tar;

            return Types.FsType.Unknown;
        }

        public static string GetFilesystemType(Types.FsType Type)
        {
            switch (Type)
            {
                case Types.FsType.Cpio: return "CPIO (ASCII)";
                case Types.FsType.Ext2: return "Ext2";
                case Types.FsType.Ext3: return "Ext3";
                case Types.FsType.Ext4: return "Ext4";
                case Types.FsType.SquashFs: return "SquashFs";
                case Types.FsType.CramFs: return "CramFs";
                case Types.FsType.RomFs: return "RomFs";
                case Types.FsType.Tar: return "Tar";
                default: return "Unknown";
            }
        }
    }

}
