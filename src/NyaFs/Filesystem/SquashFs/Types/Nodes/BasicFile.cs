using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.SquashFs.Types.Nodes
{
    class BasicFile : SqInode
    {
        uint BlockSize;

        /// <summary>
        /// True for the extended (LREG) inode layout.
        /// </summary>
        internal readonly bool IsExtended;

        private static int CalcNodeLength(uint[] BlocksSizes, bool IsExtended = false) => (IsExtended ? 0x38 : 0x20) + BlocksSizes.Length * 4;

        public BasicFile(uint Mode, uint User, uint Group, uint BlocksStart, uint FileSize, uint FragmentBlockIndex, uint FragmentBlockOffset, uint[] BlocksSizes, uint XAttrId = InvalidXAttr) : base(CalcNodeLength(BlocksSizes, XAttrId != InvalidXAttr))
        {
            IsExtended = XAttrId != InvalidXAttr;

            InodeType = IsExtended ? SqInodeType.ExtendedFile : SqInodeType.BasicFile;
            Permissions = Mode;
            GidIndex = Group;
            UidIndex = User;
            this.BlocksStart = BlocksStart;

            this.FragmentBlockIndex = FragmentBlockIndex;
            this.FragmentBlockOffset = FragmentBlockOffset;
            this.FileSize = FileSize;

            if (IsExtended)
            {
                Sparse = 0;
                HardLinkCount = 1;
                XAttrIndex = XAttrId;
            }

            int BlockListOffset = IsExtended ? 0x38 : 0x20;
            for(int i = 0; i < BlocksSizes.Length; i++)
                WriteUInt32(BlockListOffset + i * 4, BlocksSizes[i]);
        }


        public BasicFile(byte[] Data, uint BlockSize, bool IsExtended = false) : base(Data, IsExtended ? 0x38 : 0x20)
        {
            this.BlockSize = BlockSize;
            this.IsExtended = IsExtended;
        }

        /// <summary>
        /// INode size
        /// </summary>
        internal override long INodeSize => (IsExtended ? 0x38 : 0x20) + BlockCount * 4;

        /// <summary>
        /// Index in the xattr id lookup table (extended inodes only).
        /// u32 xattr (0x34)
        /// </summary>
        internal override uint XAttrIndex
        {
            get { return IsExtended ? ReadUInt32(0x34) : InvalidXAttr; }
            set { if (IsExtended) WriteUInt32(0x34, value); }
        }

        /// <summary>
        /// Number of hard links (extended inodes only).
        /// u32 nlink (0x28)
        /// </summary>
        public uint HardLinkCount
        {
            get { return IsExtended ? ReadUInt32(0x28) : 1; }
            set { if (IsExtended) WriteUInt32(0x28, value); }
        }

        /// <summary>
        /// Number of sparse bytes (extended inodes only).
        /// u64 sparse (0x20)
        /// </summary>
        public ulong Sparse
        {
            get { return IsExtended ? ReadUInt64(0x20) : 0; }
            set { if (IsExtended) WriteUInt64(0x20, value); }
        }

        /// <summary>
        /// The offset from the start of the archive where the data blocks are stored
        /// u32 blocks_start (0x10) / u64 start_block (0x10) for extended inodes
        /// </summary>
        public long BlocksStart
        {
            get { return IsExtended ? Convert.ToInt64(ReadUInt64(0x10)) : ReadUInt32(0x10); }
            set
            {
                if (IsExtended)
                    WriteUInt64(0x10, Convert.ToUInt64(value));
                else
                    WriteUInt32(0x10, Convert.ToUInt32(value));
            }
        }

        /// <summary>
        /// The index of a fragment entry in the fragment table which describes the data block the fragment of this file is stored in. 
        /// If this file does not end with a fragment, this should be 0xFFFFFFFF
        /// u32 fragment_block_index (0x14) / u32 fragment (0x2C) for extended inodes
        /// </summary>
        public uint FragmentBlockIndex
        {
            get { return IsExtended ? ReadUInt32(0x2C) : ReadUInt32(0x14); }
            set { if (IsExtended) WriteUInt32(0x2C, value); else WriteUInt32(0x14, value); }
        }

        /// <summary>
        /// The (uncompressed) offset within the fragment data block where the fragment for this file. 
        /// Information about the fragment can be found at fragment_block_index. 
        /// The size of the fragment can be found as file_size % superblock.block_size. 
        /// If this file does not end with a fragment, the value of this field is undefined (probably zero)
        /// u32 block_offset (0x18) / u32 offset (0x30) for extended inodes
        /// </summary>
        public uint FragmentBlockOffset
        {
            get { return IsExtended ? ReadUInt32(0x30) : ReadUInt32(0x18); }
            set { if (IsExtended) WriteUInt32(0x30, value); else WriteUInt32(0x18, value); }
        }

        /// <summary>
        /// The (uncompressed) size of this file
        /// u32 file_size (0x1C) / u64 file_size (0x18) for extended inodes
        /// </summary>
        public ulong FileSize
        {
            get { return IsExtended ? ReadUInt64(0x18) : ReadUInt32(0x1C); }
            set { if (IsExtended) WriteUInt64(0x18, value); else WriteUInt32(0x1C, Convert.ToUInt32(value)); }
        }

        /// <summary>
        /// The size of the fragment with id=FragmentBlockIndex
        /// </summary>
        public long FragmentSize => Convert.ToInt64(FileSize % BlockSize);

        /// <summary>
        /// Count of block sizes table
        /// </summary>
        public long BlockCount => Convert.ToInt64((FragmentBlockIndex == 0xFFFFFFFF) ? (FileSize + BlockSize - 1) / BlockSize : FileSize / BlockSize);

        /// <summary>
        /// A list of block sizes. If this file ends in a fragment, the size of this list is the number of full data blocks needed to store 
        /// file_size bytes. If this file does not have a fragment, the size of the list is the number of blocks needed to store file_size bytes, 
        /// rounded up. Each item in the list describes the (possibly compressed) size of a block. 
        /// See datablocks & fragments for information about how to interpret this size.
        /// u32 block_sizes[BlockCount]
        /// </summary>
        public uint[] BlockSizes => ReadUInt32Array(IsExtended ? 0x38 : 0x20, BlockCount);
    }
}
