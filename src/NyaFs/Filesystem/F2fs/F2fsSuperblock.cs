using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.F2fs
{
    /// <summary>
    /// F2FS superblock (f2fs_super_block), located at byte offset 1024.
    /// </summary>
    public class F2fsSuperblock
    {
        public readonly uint Magic;
        public readonly ushort MajorVer;
        public readonly ushort MinorVer;
        public readonly uint LogSectorsize;
        public readonly uint LogSectorsPerBlock;
        public readonly uint LogBlocksize;
        public readonly uint LogBlocksPerSeg;
        public readonly uint SegsPerSec;
        public readonly uint SecsPerZone;
        public readonly uint ChecksumOffset;
        public readonly ulong BlockCount;
        public readonly uint SectionCount;
        public readonly uint SegmentCount;
        public readonly uint SegmentCountCkpt;
        public readonly uint SegmentCountSit;
        public readonly uint SegmentCountNat;
        public readonly uint SegmentCountSsa;
        public readonly uint SegmentCountMain;
        public readonly uint Segment0Blkaddr;
        public readonly uint CpBlkaddr;
        public readonly uint SitBlkaddr;
        public readonly uint NatBlkaddr;
        public readonly uint SsaBlkaddr;
        public readonly uint MainBlkaddr;
        public readonly uint RootIno;
        public readonly uint NodeIno;
        public readonly uint MetaIno;
        public readonly string VolumeName;
        public readonly uint CpPayload;

        public int BlockSize => 1 << (int)LogBlocksize;
        public int BlocksPerSeg => 1 << (int)LogBlocksPerSeg;

        public F2fsSuperblock(byte[] Raw, long Offset)
        {
            if (!F2fsDetector.IsF2fs(Raw, Offset))
                throw new InvalidOperationException("Not an F2FS image");

            long B = Offset + 1024;

            Magic = Raw.ReadUInt32(B + 0x00);
            MajorVer = Raw.ReadUInt16(B + 0x04);
            MinorVer = Raw.ReadUInt16(B + 0x06);
            LogSectorsize = Raw.ReadUInt32(B + 0x08);
            LogSectorsPerBlock = Raw.ReadUInt32(B + 0x0C);
            LogBlocksize = Raw.ReadUInt32(B + 0x10);
            LogBlocksPerSeg = Raw.ReadUInt32(B + 0x14);
            SegsPerSec = Raw.ReadUInt32(B + 0x18);
            SecsPerZone = Raw.ReadUInt32(B + 0x1C);
            ChecksumOffset = Raw.ReadUInt32(B + 0x20);
            BlockCount = Raw.ReadUInt64(B + 0x24);
            SectionCount = Raw.ReadUInt32(B + 0x2C);
            SegmentCount = Raw.ReadUInt32(B + 0x30);
            SegmentCountCkpt = Raw.ReadUInt32(B + 0x34);
            SegmentCountSit = Raw.ReadUInt32(B + 0x38);
            SegmentCountNat = Raw.ReadUInt32(B + 0x3C);
            SegmentCountSsa = Raw.ReadUInt32(B + 0x40);
            SegmentCountMain = Raw.ReadUInt32(B + 0x44);
            Segment0Blkaddr = Raw.ReadUInt32(B + 0x48);
            CpBlkaddr = Raw.ReadUInt32(B + 0x4C);
            SitBlkaddr = Raw.ReadUInt32(B + 0x50);
            NatBlkaddr = Raw.ReadUInt32(B + 0x54);
            SsaBlkaddr = Raw.ReadUInt32(B + 0x58);
            MainBlkaddr = Raw.ReadUInt32(B + 0x5C);
            RootIno = Raw.ReadUInt32(B + 0x60);
            NodeIno = Raw.ReadUInt32(B + 0x64);
            MetaIno = Raw.ReadUInt32(B + 0x68);
            CpPayload = Raw.ReadUInt32(B + 0x2A0);

            var NameBytes = new byte[32];
            Array.Copy(Raw, B + 0x7C, NameBytes, 0, 32);
            VolumeName = Encoding.Unicode.GetString(NameBytes).TrimEnd('\0');
        }
    }
}
