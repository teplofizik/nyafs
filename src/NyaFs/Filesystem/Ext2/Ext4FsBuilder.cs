using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.Ext2
{
    /// <summary>
    /// ext4 filesystem builder: the ext3 layout (journal) plus the EXTENTS feature.
    /// Data blocks of every inode are recorded as an extent tree (i_block root + index blocks
    /// when more than 4 extents are needed) instead of the classic block map.
    /// </summary>
    public class Ext4FsBuilder : Ext3FsBuilder
    {
        const uint INCOMPAT_EXTENTS = 0x40;
        const uint EXT4_EXTENTS_FL = 0x80000;

        const ushort EXTENT_MAGIC = 0xF30A;
        const int EXTENT_HEADER_SIZE = 12;
        const int EXTENT_ENTRY_SIZE = 12;
        const int ROOT_MAX_ENTRIES = 4;      // i_block holds a 12-byte header + 4 * 12-byte entries
        const int MAX_INITIALIZED_LEN = 32768;

        public Ext4FsBuilder(uint DiskBlockSize) : base(DiskBlockSize)
        {
            Superblock.FeatureIncompatible |= INCOMPAT_EXTENTS;
        }

        internal override void StoreBlocks(Types.ExtINode Node, List<uint> Blocks)
        {
            if (Blocks.Count == 0)
                return;

            var Exts = BuildExtents(Blocks);
            Node.Flags |= EXT4_EXTENTS_FL;

            uint ExtraBlocks = 0;
            WriteExtentTree(Node, Exts, ref ExtraBlocks);

            Node.BlocksLo = Convert.ToUInt32((Blocks.Count + ExtraBlocks) * (Superblock.BlockSize / 512));
        }

        struct Extent
        {
            public uint Logical;
            public ushort Len;
            public ulong Phys;
        }

        /// <summary>
        /// Merge runs of physically consecutive blocks into extents.
        /// </summary>
        private static List<Extent> BuildExtents(List<uint> Blocks)
        {
            var Res = new List<Extent>();

            int i = 0;
            while (i < Blocks.Count)
            {
                int Len = 1;
                while ((i + Len < Blocks.Count) && (Blocks[i + Len] == Blocks[i + Len - 1] + 1) && (Len < MAX_INITIALIZED_LEN))
                    Len++;

                Res.Add(new Extent { Logical = Convert.ToUInt32(i), Len = Convert.ToUInt16(Len), Phys = Blocks[i] });
                i += Len;
            }

            return Res;
        }

        private void WriteExtentTree(Types.ExtINode Node, List<Extent> Exts, ref uint ExtraBlocks)
        {
            int Cap = (int)((Superblock.BlockSize - EXTENT_HEADER_SIZE) / EXTENT_ENTRY_SIZE);

            int Depth = 0;
            long Capacity = ROOT_MAX_ENTRIES;
            while (Capacity < Exts.Count)
            {
                Depth++;
                Capacity = ROOT_MAX_ENTRIES * PowLong(Cap, Depth);
            }

            var Root = new byte[60];
            WriteExtentNode(Exts, Depth, Root, 0, ROOT_MAX_ENTRIES, ref ExtraBlocks);
            Node.SetINodeBlockArea(Root);
        }

        private void WriteExtentNode(List<Extent> Exts, int Depth, byte[] Buffer, int Offset, int MaxEntries, ref uint ExtraBlocks)
        {
            int Cap = (int)((Superblock.BlockSize - EXTENT_HEADER_SIZE) / EXTENT_ENTRY_SIZE);

            // For a leaf the header entry count is the number of extents; for an index it is
            // the number of child blocks (which may be far fewer than the extent count).
            int ChildCount = 0;
            int Entries = Exts.Count;
            if (Depth > 0)
            {
                long PerChild = PowLong(Cap, Depth);
                ChildCount = Convert.ToInt32((Exts.Count + PerChild - 1) / PerChild);
                Entries = ChildCount;
            }

            WriteU16(Buffer, Offset + 0, EXTENT_MAGIC);
            WriteU16(Buffer, Offset + 2, Convert.ToUInt16(Entries));    // entries
            WriteU16(Buffer, Offset + 4, Convert.ToUInt16(MaxEntries)); // max
            WriteU16(Buffer, Offset + 6, Convert.ToUInt16(Depth));      // depth
            WriteU32(Buffer, Offset + 8, 0);                            // generation

            int Base = Offset + EXTENT_HEADER_SIZE;

            if (Depth == 0)
            {
                for (int i = 0; i < Exts.Count; i++)
                {
                    int E = Base + i * EXTENT_ENTRY_SIZE;
                    WriteU32(Buffer, E + 0, Exts[i].Logical);
                    WriteU16(Buffer, E + 4, Exts[i].Len);
                    WriteU16(Buffer, E + 6, Convert.ToUInt16(Exts[i].Phys >> 32));
                    WriteU32(Buffer, E + 8, Convert.ToUInt32(Exts[i].Phys & 0xFFFFFFFF));
                }
            }
            else
            {
                long PerChild = PowLong(Cap, Depth);

                for (int c = 0; c < ChildCount; c++)
                {
                    int Start = Convert.ToInt32(c * PerChild);
                    int Count = Convert.ToInt32(Math.Min(PerChild, Exts.Count - Start));
                    var ChildExts = Exts.GetRange(Start, Count);

                    var ChildBlock = GetNewBlock();
                    ExtraBlocks++;

                    var ChildBuf = new byte[Superblock.BlockSize];
                    WriteExtentNode(ChildExts, Depth - 1, ChildBuf, 0, Cap, ref ExtraBlocks);
                    WriteArray(ChildBlock * Superblock.BlockSize, ChildBuf, ChildBuf.Length);

                    int E = Base + c * EXTENT_ENTRY_SIZE;
                    WriteU32(Buffer, E + 0, ChildExts[0].Logical); // ei_block
                    WriteU32(Buffer, E + 4, ChildBlock);           // ei_leaf_lo
                    WriteU16(Buffer, E + 8, 0);                    // ei_leaf_hi
                    WriteU16(Buffer, E + 10, 0);                   // ei_unused
                }
            }
        }

        private static long PowLong(int Base, int Exp)
        {
            long Res = 1;
            for (int i = 0; i < Exp; i++)
                Res *= Base;
            return Res;
        }

        private static void WriteU16(byte[] Data, int Offset, ushort Value)
        {
            Data[Offset + 0] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
        }

        private static void WriteU32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset + 0] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }
    }
}
