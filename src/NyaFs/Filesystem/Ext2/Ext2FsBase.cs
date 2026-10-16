using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.Ext2
{
    public class Ext2FsBase : RawPacket
    {
        internal Types.ExtSuperBlock Superblock;

        /// <summary>
        /// Inode structure size. ext2/ext3 => 128, ext4 (EXTRA_ISIZE) => 256 (taken from the superblock).
        /// </summary>
        protected uint INodeSize => ((Superblock.RevLevel >= 1) && (Superblock.INodeSize != 0)) ? Superblock.INodeSize : 128;

        /// <summary>
        /// Offset of the group descriptor table: the block right after the superblock block
        /// (block 2 for 1k-block filesystems, block 1 otherwise).
        /// </summary>
        protected uint GdtOffset => (Superblock.FirstDataBlock + 1) * Superblock.BlockSize;

        /// <summary>
        /// Group descriptor size (0x20 for 32-bit, 0x40 for 64-bit feature).
        /// </summary>
        protected uint GroupDescSize => (Superblock.GroupDescSize != 0) ? Superblock.GroupDescSize : 0x20;

        public Ext2FsBase(byte[] Data) : base(Data)
        {
            Superblock = new Types.ExtSuperBlock(Raw, 0x400);
        }

        /// <summary>
        /// Block group descriptor (the table location/size depends on the block size and 64bit feature).
        /// </summary>
        /// <param name="Id"></param>
        /// <returns></returns>
        internal Types.ExtBlockGroup GetBlockGroup(uint Id) => new Types.ExtBlockGroup(Raw, Id, GdtOffset + Id * GroupDescSize);

        internal Types.ExtBlockGroup GetBlockGroupByNodeId(uint Id) => GetBlockGroup(Id / Superblock.InodesPerGroup);
        internal Types.ExtBlockGroup GetBlockGroupByBlockId(uint Id) => GetBlockGroup(Id / Superblock.BlocksPerGroup);

        internal Types.ExtINode GetINode(uint Id)
        {
            var BGIndex = (Id - 1) / Superblock.InodesPerGroup;
            var INIndex = (Id - 1) % Superblock.InodesPerGroup;

            var BG = GetBlockGroup(BGIndex);
            return new Types.ExtINode(Id, Raw, (BG.INodeTableLo * Superblock.BlockSize + INodeSize * INIndex), INodeSize);
        }

        /// <summary>
        /// Read the data blocks of an inode. Default implementation is the classic ext2/ext3 block map
        /// (direct + single/double/triple indirect); ext4 overrides it to walk the extent tree.
        /// </summary>
        internal virtual byte[] GetINodeBlockContent(Types.ExtINode Node)
        {
            byte[] Res = new byte[Node.SizeLo];
            var Blocks = Node.Block;

            long DataOffset = 0;
            {
                // Direct blocks
                DataOffset = ReadToArrayFromBlockTable(Res, DataOffset, Node.SizeLo, Blocks, 12);
                // Direct data block addressing!..
                if (DataOffset == Node.SizeLo)
                    return Res;
            }

            if (Blocks[12] != 0)
            {
                // Indirect blocks 1 level
                var IndirectTable = ReadUInt32Array(Blocks[12] * Superblock.BlockSize, Superblock.BlockSize / 4);
                DataOffset = ReadToArrayFromBlockTable(Res, DataOffset, Node.SizeLo, IndirectTable, IndirectTable.Length);

                if (DataOffset == Node.SizeLo)
                    return Res;
            }

            if (Blocks[13] != 0)
            {
                // Indirect blocks 2 level
                var IndirectTable2 = ReadUInt32Array(Blocks[13] * Superblock.BlockSize, Superblock.BlockSize / 4);
                DataOffset = ReadIndirectDataTable(Res, DataOffset, Node.SizeLo, IndirectTable2, IndirectTable2.Length);

                if (DataOffset == Node.SizeLo)
                    return Res;
            }

            if (Blocks[14] != 0)
            {
                // Indirect blocks 3 level
                var IndirectTable3Offset = Blocks[14] * Superblock.BlockSize;
                var IndirectTable3 = ReadUInt32Array(Blocks[14] * Superblock.BlockSize, Superblock.BlockSize / 4);

                foreach (var IndirectOffset2 in IndirectTable3)
                {
                    var IndirectTable2 = ReadUInt32Array(IndirectOffset2, Superblock.BlockSize / 4);

                    DataOffset = ReadIndirectDataTable(Res, DataOffset, Node.SizeLo, IndirectTable2, IndirectTable2.Length);

                    if (DataOffset == Node.SizeLo)
                        return Res;
                }
            }

            if (DataOffset != Node.SizeLo)
                Console.WriteLine($"Invalid blocks content in Node");

            return Res;
        }

        private long ReadIndirectDataTable(byte[] Data, long Offset, long Size, uint[] IndirectTable2, int TableLength)
        {
            foreach (var IndirectOffset in IndirectTable2)
            {
                if (IndirectOffset > 0)
                {
                    var IndirectTable = ReadUInt32Array(IndirectOffset * Superblock.BlockSize, Superblock.BlockSize / 4);

                    Offset = ReadToArrayFromBlockTable(Data, Offset, Size, IndirectTable, IndirectTable.Length);

                    if (Offset == Size)
                        return Offset;
                }
            }

            return Offset;
        }

        private long ReadToArrayFromBlockTable(byte[] Data, long Offset, long Size, uint[] Blocks, int TableLength)
        {
            for (int i = 0; i < TableLength; i++)
            {
                var B = Blocks[i];
                if (B != 0)
                {
                    var ToRead = (Size - Offset);
                    var BlockOffset = B * Superblock.BlockSize;
                    var TR = (ToRead > Superblock.BlockSize) ? Superblock.BlockSize : ToRead;
                    var ReadOutData = ReadArray(BlockOffset, TR);

                    Data.WriteArray(Offset, ReadOutData, TR);

                    Offset += TR;
                    if (Offset == Size)
                        return Offset;
                }
                else
                {
                    Offset += Superblock.BlockSize;

                    if (Offset == Size)
                        return Offset;
                }
            }

            return Offset;
        }

        internal byte[] GetINodeContent(Types.ExtINode Node)
        {
            var Size = Node.SizeLo;
            var Blocks = Node.Block;
            var Type = Node.NodeType;

            if (Size == 0)
                return new byte[] { };

            // Тип ноды
            switch (Type)
            {
                case Types.ExtINodeType.LINK:
                    if (Size > 60)
                        return GetINodeBlockContent(Node);
                    else
                        return Node.BlockRaw;

                default:
                    return GetINodeBlockContent(Node);
            }
        }
    }
}