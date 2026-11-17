using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Ext2
{
    /// <summary>
    /// ext4 filesystem reader. Inodes without EXT4_EXTENTS_FL use the classic ext2 block map
    /// (inherited); inodes with the flag are read by walking the extent tree stored in i_block.
    /// </summary>
    public class Ext4FsReader : Ext2FsReader
    {
        const uint EXT4_EXTENTS_FL = 0x80000;

        const ushort EXTENT_MAGIC = 0xF30A;
        const int EXTENT_HEADER_SIZE = 12;
        const int EXTENT_ENTRY_SIZE = 12;
        const int EXTENT_MAX_INITIALIZED = 32768;

        public Ext4FsReader(byte[] Data) : base(Data) { }

        public Ext4FsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }

        internal override byte[] GetINodeBlockContent(Types.ExtINode Node)
        {
            if ((Node.Flags & EXT4_EXTENTS_FL) != 0)
                return ReadExtents(Node);

            return base.GetINodeBlockContent(Node);
        }

        private byte[] ReadExtents(Types.ExtINode Node)
        {
            var Res = new byte[Node.SizeLo];
            ReadExtentNode(Node.INodeBlockArea, 0, Res);
            return Res;
        }

        /// <summary>
        /// Walk one node of the extent tree (the inode i_block root or a full index block) and copy
        /// the referenced blocks into the output. Uninitialized extents and gaps stay zero-filled.
        /// </summary>
        private void ReadExtentNode(byte[] NodeData, int Offset, byte[] Out)
        {
            var Magic = (ushort)(NodeData[Offset] | (NodeData[Offset + 1] << 8));
            if (Magic != EXTENT_MAGIC)
                throw new InvalidOperationException($"Invalid extent tree magic 0x{Magic:x4}");

            int Entries = NodeData[Offset + 2] | (NodeData[Offset + 3] << 8);
            int Depth = NodeData[Offset + 6] | (NodeData[Offset + 7] << 8);
            int Base = Offset + EXTENT_HEADER_SIZE;

            for (int i = 0; i < Entries; i++)
            {
                int E = Base + i * EXTENT_ENTRY_SIZE;

                if (Depth == 0)
                    ReadExtentLeaf(NodeData, E, Out);
                else
                    ReadExtentIndex(NodeData, E, Out);
            }
        }

        private void ReadExtentLeaf(byte[] NodeData, int E, byte[] Out)
        {
            long Logical = ReadUInt32At(NodeData, E);
            int Len = NodeData[E + 4] | (NodeData[E + 5] << 8);
            long StartHi = (ushort)(NodeData[E + 6] | (NodeData[E + 7] << 8));
            long StartLo = ReadUInt32At(NodeData, E + 8);
            long PhysBlock = (StartHi << 32) | StartLo;

            bool Uninitialized = Len > EXTENT_MAX_INITIALIZED;
            int Count = Uninitialized ? (Len - EXTENT_MAX_INITIALIZED) : Len;

            for (int b = 0; b < Count; b++)
            {
                long FileOffset = (Logical + b) * Superblock.BlockSize;
                if (FileOffset >= Out.Length)
                    break;

                int ToRead = (int)Math.Min(Superblock.BlockSize, Out.Length - FileOffset);
                if (!Uninitialized)
                {
                    var Data = ReadArray((PhysBlock + b) * Superblock.BlockSize, ToRead);
                    Array.Copy(Data, 0, Out, (int)FileOffset, ToRead);
                }
            }
        }

        private void ReadExtentIndex(byte[] NodeData, int E, byte[] Out)
        {
            long ChildLo = ReadUInt32At(NodeData, E + 4);
            long ChildHi = (ushort)(NodeData[E + 8] | (NodeData[E + 9] << 8));
            long ChildBlock = (ChildHi << 32) | ChildLo;

            var ChildData = ReadArray(ChildBlock * Superblock.BlockSize, Superblock.BlockSize);
            ReadExtentNode(ChildData, 0, Out);
        }

        private static long ReadUInt32At(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8) | (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));
    }
}
