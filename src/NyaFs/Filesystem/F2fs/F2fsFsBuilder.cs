using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.F2fs
{
    /// <summary>
    /// F2FS image builder (Milestone 1: empty, mountable filesystem, mkfs-like layout).
    /// Writes superblock, checkpoint pack (compacted summaries + NAT/SIT journals + nat_bits),
    /// NAT, root inode and root dentry. SIT/SSA areas are left zero.
    /// </summary>
    public class F2fsFsBuilder : Universal.IFilesystemBuilder
    {
        public const int BlockSize = 4096;
        public const int BlocksPerSeg = 512;

        public readonly long TotalBytes;
        public uint RootIno = 3;
        public uint NodeIno = 1;
        public uint MetaIno = 2;
        public uint ReservedSegments = 8;
        public uint OverprovisionSegments = 10;

        // geometry
        private int Segment0Blkaddr;
        private int CpBlkaddr, SitBlkaddr, NatBlkaddr, SsaBlkaddr, MainBlkaddr;
        private int SectionCount, SegmentCount, SegCkpt, SegSit, SegNat, SegSsa, SegMain;

        private byte[] Image;
        private uint CurNodeHot, CurNodeWarm, CurNodeCold;
        private uint CurDataHot, CurDataWarm, CurDataCold;

        private class Node
        {
            public string Name;
            public bool IsDir;
            public byte[] Content;
            public readonly List<Node> Children = new List<Node>();
        }

        private Node Root;

        public F2fsFsBuilder(long TotalBytes)
        {
            this.TotalBytes = TotalBytes;
            ComputeGeometry();
        }

        public void Directory(string Path, uint User, uint Group, uint Mode)
        {
            if ((Path == "/") || (Path == ".") || (Path == "") || (Path == null))
            {
                if (Root == null)
                    Root = new Node { Name = "", IsDir = true };
                return;
            }
            throw new NotSupportedException("F2FS builder M1 supports only an empty root filesystem");
        }

        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) => throw new NotSupportedException("F2FS builder M1 does not write files yet");
        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) => throw new NotSupportedException("F2FS builder M1 does not write files yet");
        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) => throw new NotSupportedException("F2FS builder M1 does not write files yet");
        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) => throw new NotSupportedException("F2FS builder M1 does not write files yet");
        public void Fifo(string Path, uint User, uint Group, uint Mode) => throw new NotSupportedException("F2FS builder M1 does not write files yet");
        public void Socket(string Path, uint User, uint Group, uint Mode) => throw new NotSupportedException("F2FS builder M1 does not write files yet");

        // ---- geometry ----

        private static int AlignUp(int V, int A) => ((V + A - 1) / A) * A;

        private void ComputeGeometry()
        {
            int Blocks = (int)(TotalBytes / BlockSize);
            int SegmentSizeBytes = BlockSize * BlocksPerSeg;
            int ZoneSizeBytes = SegmentSizeBytes;

            int ZoneAlign = ((0 + 2 * BlockSize + ZoneSizeBytes - 1) / ZoneSizeBytes) * ZoneSizeBytes;
            Segment0Blkaddr = ZoneAlign / BlockSize;
            CpBlkaddr = Segment0Blkaddr;

            SegmentCount = (int)((TotalBytes - ZoneAlign) / SegmentSizeBytes);
            SegCkpt = 2;

            int BlocksForSit = AlignUp(SegmentCount, 455);
            SegSit = ((BlocksForSit + BlocksPerSeg - 1) / BlocksPerSeg) * 2;

            int BlocksForNat = AlignUp(SegmentCount, 455);
            SegNat = ((BlocksForNat + BlocksPerSeg - 1) / BlocksPerSeg) * 2;

            int AfterMeta = SegmentCount - (SegCkpt + SegSit + SegNat);
            SegSsa = (AfterMeta + BlocksPerSeg - 1) / BlocksPerSeg;

            int TotalMeta = SegCkpt + SegSit + SegNat + SegSsa;
            SitBlkaddr = Segment0Blkaddr + SegCkpt * BlocksPerSeg;
            NatBlkaddr = SitBlkaddr + SegSit * BlocksPerSeg;
            SsaBlkaddr = NatBlkaddr + SegNat * BlocksPerSeg;
            MainBlkaddr = SsaBlkaddr + SegSsa * BlocksPerSeg;

            SectionCount = SegmentCount - TotalMeta;
            SegMain = SectionCount;

            CurNodeHot = 0;
            CurNodeWarm = 1;
            CurNodeCold = 2;
            CurDataHot = 3;
            CurDataCold = (uint)Math.Max((SegMain >> 2) - 1, 3);
            CurDataWarm = (uint)Math.Max((SegMain >> 1) - 1, (int)CurDataCold + 1);
        }

        // ---- build ----

        public byte[] GetFilesystemImage()
        {
            Image = new byte[TotalBytes];

            WriteSuperblock();
            WriteCheckpointPack();
            WriteNatArea();
            WriteRootInode();
            WriteRootDentry();

            return Image;
        }

        private void WriteSuperblock()
        {
            var Sb = new byte[3072];
            PutU32(Sb, 0x00, 0xF2F52010);
            PutU16(Sb, 0x04, 1);
            PutU16(Sb, 0x06, 16);
            PutU32(Sb, 0x08, 9);
            PutU32(Sb, 0x0C, 3);
            PutU32(Sb, 0x10, 12);
            PutU32(Sb, 0x14, 9);
            PutU32(Sb, 0x18, 1);
            PutU32(Sb, 0x1C, 1);
            PutU32(Sb, 0x20, 0);
            PutU64(Sb, 0x24, (ulong)(TotalBytes / BlockSize));
            PutU32(Sb, 0x2C, (uint)SectionCount);
            PutU32(Sb, 0x30, (uint)SegmentCount);
            PutU32(Sb, 0x34, (uint)SegCkpt);
            PutU32(Sb, 0x38, (uint)SegSit);
            PutU32(Sb, 0x3C, (uint)SegNat);
            PutU32(Sb, 0x40, (uint)SegSsa);
            PutU32(Sb, 0x44, (uint)SegMain);
            PutU32(Sb, 0x48, (uint)Segment0Blkaddr);
            PutU32(Sb, 0x4C, (uint)CpBlkaddr);
            PutU32(Sb, 0x50, (uint)SitBlkaddr);
            PutU32(Sb, 0x54, (uint)NatBlkaddr);
            PutU32(Sb, 0x58, (uint)SsaBlkaddr);
            PutU32(Sb, 0x5C, (uint)MainBlkaddr);
            PutU32(Sb, 0x60, RootIno);
            PutU32(Sb, 0x64, NodeIno);
            PutU32(Sb, 0x68, MetaIno);
            PutU32(Sb, 0x2A0, 0);                    // cp_payload

            Array.Copy(Sb, 0, Image, 1024, Sb.Length);
            Array.Copy(Sb, 0, Image, 1024 + BlockSize, Sb.Length);
        }

        private void WriteCheckpointPack()
        {
            int SitBmBytes = (SegSit / 2) * BlocksPerSeg / 8;
            int NatBmBytes = (SegNat / 2) * BlocksPerSeg / 8;

            var Cp = BuildCheckpoint(0x1, SitBmBytes, NatBmBytes);
            Array.Copy(Cp, 0, Image, (long)CpBlkaddr * BlockSize, BlockSize);

            WriteCompactedSummary(CpBlkaddr + 1);
            WriteNodeSummary(CpBlkaddr + 2, true);
            WriteNodeSummary(CpBlkaddr + 3, false);
            WriteNodeSummary(CpBlkaddr + 4, false);

            Array.Copy(Cp, 0, Image, (long)(CpBlkaddr + 5) * BlockSize, BlockSize);

            uint CpCrcVal = ReadU32(Cp, 4092);
            ulong Ver = ReadU64(Cp, 0x00);
            WriteNatBits(Ver | ((ulong)CpCrcVal << 32));

            var Cp2 = BuildCheckpoint(0, SitBmBytes, NatBmBytes);
            Array.Copy(Cp2, 0, Image, (long)(CpBlkaddr + 512) * BlockSize, BlockSize);
            Array.Copy(Cp2, 0, Image, (long)(CpBlkaddr + 512 + 5) * BlockSize, BlockSize);
        }

        private byte[] BuildCheckpoint(ulong Version, int SitBmBytes, int NatBmBytes)
        {
            var Cp = new byte[BlockSize];
            PutU64(Cp, 0x00, Version);
            PutU64(Cp, 0x08, (ulong)(SegMain - (int)OverprovisionSegments) * BlocksPerSeg);
            PutU64(Cp, 0x10, 2);
            PutU32(Cp, 0x18, ReservedSegments);
            PutU32(Cp, 0x1C, OverprovisionSegments);
            PutU32(Cp, 0x20, (uint)(SegMain - 6));

            for (int i = 0; i < 8; i++)
            {
                PutU32(Cp, 0x24 + i * 4, i < 3 ? new uint[] { CurNodeHot, CurNodeWarm, CurNodeCold }[i] : 0xFFFFFFFF);
                PutU32(Cp, 0x54 + i * 4, i < 3 ? new uint[] { CurDataHot, CurDataWarm, CurDataCold }[i] : 0xFFFFFFFF);
                PutU16(Cp, 0x44 + i * 2, (ushort)(i == 0 ? 1 : 0));
                PutU16(Cp, 0x74 + i * 2, (ushort)(i == 0 ? 1 : 0));
            }

            PutU32(Cp, 0x84, 0x185);                 // UMOUNT | COMPACT_SUM | NAT_BITS | TRIMMED
            PutU32(Cp, 0x88, 6);
            PutU32(Cp, 0x8C, 1);
            PutU32(Cp, 0x90, 1);
            PutU32(Cp, 0x94, 1);
            PutU32(Cp, 0x98, RootIno + 1);
            PutU32(Cp, 0x9C, (uint)SitBmBytes);
            PutU32(Cp, 0xA0, (uint)NatBmBytes);
            PutU32(Cp, 0xA4, 4092);

            PutU32(Cp, 4092, Crc32(0xF2F52010, Cp, 0, 4092));
            return Cp;
        }

        private void WriteCompactedSummary(int BlkAddr)
        {
            long Off = (long)BlkAddr * BlockSize;
            var Sum = new byte[BlockSize];

            PutU16(Sum, 0, 1);
            PutU32(Sum, 2, RootIno);
            Sum[6] = 0;
            PutU32(Sum, 7, RootIno);
            PutU32(Sum, 11, (uint)(MainBlkaddr + CurNodeHot * BlocksPerSeg));

            PutU16(Sum, 507, 6);
            WriteSitJournalEntry(Sum, 509 + 0 * 78, CurNodeHot, (ushort)((3 << 10) | 1), true);
            WriteSitJournalEntry(Sum, 509 + 1 * 78, CurNodeWarm, (ushort)(4 << 10), false);
            WriteSitJournalEntry(Sum, 509 + 2 * 78, CurNodeCold, (ushort)(5 << 10), false);
            WriteSitJournalEntry(Sum, 509 + 3 * 78, CurDataHot, (ushort)((0 << 10) | 1), true);
            WriteSitJournalEntry(Sum, 509 + 4 * 78, CurDataWarm, (ushort)(1 << 10), false);
            WriteSitJournalEntry(Sum, 509 + 5 * 78, CurDataCold, (ushort)(2 << 10), false);

            PutU32(Sum, 1014, RootIno);
            Sum[1018] = 0;
            PutU16(Sum, 1019, 0);

            Array.Copy(Sum, 0, Image, Off, BlockSize);
        }

        private static void WriteSitJournalEntry(byte[] Sum, int Off, uint Segno, ushort VBlocks, bool Valid)
        {
            PutU32(Sum, Off, Segno);
            PutU16(Sum, Off + 4, VBlocks);
            if (Valid)
                Sum[Off + 6] = 0x80;
        }

        private void WriteNodeSummary(int BlkAddr, bool HasEntry)
        {
            long Off = (long)BlkAddr * BlockSize;
            var Sum = new byte[BlockSize];

            if (HasEntry)
            {
                PutU32(Sum, 0, RootIno);
                Sum[4] = 0;
                PutU16(Sum, 5, 0);
            }
            Sum[4091] = 1;                            // SUM_TYPE_NODE

            Array.Copy(Sum, 0, Image, Off, BlockSize);
        }

        private void WriteNatBits(ulong CpVerCrc)
        {
            int NatBitsBytes = SegNat << 5;
            int NatBitsBlocks = ((NatBitsBytes << 1) + 8 + BlockSize - 1) / BlockSize;
            int Blk = Segment0Blkaddr + BlocksPerSeg - NatBitsBlocks;

            long Off = (long)Blk * BlockSize;
            PutU64(Image, Off, CpVerCrc);

            for (int i = 8; i < 8 + NatBitsBytes; i++)
                Image[Off + i] = 0x00;

            for (int i = 0; i < NatBitsBytes; i++)
                Image[Off + 8 + NatBitsBytes + i] = 0xFF;
            Image[Off + 8 + NatBitsBytes] = 0xFE;
        }

        private void WriteNatArea()
        {
            long Off = (long)NatBlkaddr * BlockSize;

            PutU32(Image, Off + 1 * 9 + 1, NodeIno);
            PutU32(Image, Off + 1 * 9 + 5, 1);

            PutU32(Image, Off + 2 * 9 + 1, MetaIno);
            PutU32(Image, Off + 2 * 9 + 5, 1);

            PutU32(Image, Off + 3 * 9 + 1, RootIno);
            PutU32(Image, Off + 3 * 9 + 5, (uint)MainBlkaddr);
        }

        private void WriteRootInode()
        {
            long Off = (long)MainBlkaddr * BlockSize;
            var N = new byte[BlockSize];

            PutU16(N, 0x00, 0x41ED);
            PutU32(N, 0x0C, 2);
            PutU64(N, 0x10, BlockSize);
            PutU64(N, 0x18, 2);
            PutU32(N, 0x48, 1);
            PutU32(N, 0x168, (uint)(MainBlkaddr + CurDataHot * BlocksPerSeg));

            PutU32(N, 0xFE8, RootIno);
            PutU32(N, 0xFEC, RootIno);
            PutU32(N, 0xFF4, 1);
            PutU32(N, 0xFFC, (uint)(MainBlkaddr + 1));

            Array.Copy(N, 0, Image, Off, BlockSize);
        }

        private void WriteRootDentry()
        {
            long Off = (long)(MainBlkaddr + CurDataHot * BlocksPerSeg) * BlockSize;
            var D = new byte[BlockSize];

            D[0] = 0x03;
            PutU32(D, 30 + 0 * 11 + 4, RootIno);
            PutU16(D, 30 + 0 * 11 + 8, 1);
            D[30 + 0 * 11 + 10] = 2;
            D[2384 + 0 * 8] = (byte)'.';

            PutU32(D, 30 + 1 * 11 + 4, RootIno);
            PutU16(D, 30 + 1 * 11 + 8, 2);
            D[30 + 1 * 11 + 10] = 2;
            D[2384 + 1 * 8] = (byte)'.';
            D[2384 + 1 * 8 + 1] = (byte)'.';

            Array.Copy(D, 0, Image, Off, BlockSize);
        }

        // ---- primitives ----

        private static void PutU16(byte[] D, int O, ushort V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
        }

        private static void PutU32(byte[] D, int O, uint V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
            D[O + 2] = (byte)(V >> 16);
            D[O + 3] = (byte)(V >> 24);
        }

        private static void PutU16(byte[] D, long O, ushort V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
        }

        private static void PutU32(byte[] D, long O, uint V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
            D[O + 2] = (byte)(V >> 16);
            D[O + 3] = (byte)(V >> 24);
        }

        private static void PutU64(byte[] D, int O, ulong V)
        {
            for (int i = 0; i < 8; i++)
                D[O + i] = (byte)(V >> (i * 8));
        }

        private static void PutU64(byte[] D, long O, ulong V)
        {
            for (int i = 0; i < 8; i++)
                D[O + i] = (byte)(V >> (i * 8));
        }

        private static uint ReadU32(byte[] D, int O) => BitConverter.ToUInt32(D, O);
        private static ulong ReadU64(byte[] D, int O) => BitConverter.ToUInt64(D, O);

        private static uint Crc32(uint Crc, byte[] Data, int Off, int Len)
        {
            for (int i = 0; i < Len; i++)
            {
                Crc ^= Data[Off + i];
                for (int b = 0; b < 8; b++)
                    Crc = ((Crc & 1) != 0) ? ((Crc >> 1) ^ 0xEDB88320u) : (Crc >> 1);
            }
            return Crc;
        }
    }
}
