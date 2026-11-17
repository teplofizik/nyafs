using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.SquashFs.Types.Nodes
{
    class BasicDirectory : SqInode
    {
        /// <summary>
        /// True when this directory is written using the extended (LDIR) inode layout.
        /// </summary>
        internal readonly bool IsExtended;

        public BasicDirectory(uint Mode, uint User, uint Group, uint DirBlockStart, uint DirBlockOffset, uint HardLinkCount, uint DirEntriesFullSize, uint ParentINodeNumber, uint XAttrId = InvalidXAttr) : base(XAttrId != InvalidXAttr ? 0x28 : 0x20)
        {
            IsExtended = XAttrId != InvalidXAttr;

            InodeType = IsExtended ? SqInodeType.ExtendedDirectory : SqInodeType.BasicDirectory;
            Permissions = Mode;
            GidIndex = Group;
            UidIndex = User;

            this.DirBlockStart = DirBlockStart;

            FileSize = DirEntriesFullSize;
            BlockOffset = DirBlockOffset;
            this.HardLinkCount = HardLinkCount;
            this.ParentINodeNumber = ParentINodeNumber;

            if (IsExtended)
            {
                IndexCount = 0;
                XAttrIndex = XAttrId;
            }
        }

        public BasicDirectory(byte[] Data, bool IsExtended = false) : base(Data, IsExtended ? 0x28 : 0x20)
        {
            this.IsExtended = IsExtended;
        }

        /// <summary>
        /// INode size
        /// </summary>
        internal override long INodeSize => IsExtended ? 0x28 : 0x20;

        /// <summary>
        /// Index in the xattr id lookup table (extended inodes only).
        /// u32 xattr (0x24)
        /// </summary>
        internal override uint XAttrIndex
        {
            get { return IsExtended ? ReadUInt32(0x24) : InvalidXAttr; }
            set { if (IsExtended) WriteUInt32(0x24, value); }
        }

        /// <summary>
        /// Number of directory index entries following the inode (extended inodes only).
        /// u16 index_count (0x20)
        /// </summary>
        public uint IndexCount
        {
            get { return IsExtended ? ReadUInt16(0x20) : 0u; }
            set { if (IsExtended) WriteUInt16(0x20, value); }
        }

        /// <summary>
        /// The location of the of the block in the Directory Table where the directory entry information starts
        /// u32 dir_block_start (0x10) / u32 start_block (0x18) for extended inodes
        /// </summary>
        public uint DirBlockStart
        {
            get { return ReadUInt32(IsExtended ? 0x18 : 0x10); }
            set { WriteUInt32(IsExtended ? 0x18 : 0x10, value); }
        }

        /// <summary>
        /// The number of hard links to this directory. 
        /// Note that for historical reasons, the hard link count of a directory includes the number of entries in the directory and is initialized to 2 for an empty directory. 
        /// I.e. a directory with N entries has at least N + 2 link count.
        /// u32 hard_link_count (0x14) / u32 nlink (0x10) for extended inodes
        /// </summary>
        public uint HardLinkCount
        {
            get { return ReadUInt32(IsExtended ? 0x10 : 0x14); }
            set { WriteUInt32(IsExtended ? 0x10 : 0x14, value); }
        }

        /// <summary>
        /// Total (uncompressed) size in bytes of the entries in the Directory Table, without the virtual "." and ".." bytes.
        /// On disk the value includes the extra 3 bytes for the virtual "." and ".." entries.
        /// u16 file_size (0x18) / u32 file_size (0x14) for extended inodes
        /// </summary>
        public uint FileSize
        {
            get
            {
                if (IsExtended)
                    return Convert.ToUInt32(ReadUInt32(0x14) - 3);

                return Convert.ToUInt32(ReadUInt16(0x18) + 3);
            }
            set
            {
                if (IsExtended)
                    WriteUInt32(0x14, Convert.ToUInt32(value + 3));
                else
                    WriteUInt16(0x18, Convert.ToUInt32(value > 3 ? value - 3 : 0));
            }
        }

        /// <summary>
        /// The (uncompressed) offset within the block in the Directory Table where the directory entry information starts
        /// u16 block_offset (0x1A) / u16 offset (0x22) for extended inodes
        /// </summary>
        public uint BlockOffset
        {
            get { return ReadUInt16(IsExtended ? 0x22 : 0x1A); }
            set { WriteUInt16(IsExtended ? 0x22 : 0x1A, value); }
        }

        /// <summary>
        /// The inode_number of the parent of this directory. If this is the root directory, this will be 1
        /// u32 parent_inode_number (0x1C)
        /// </summary>
        public uint ParentINodeNumber
        {
            get { return ReadUInt32(0x1C); }
            set { WriteUInt32(0x1C, value); }
        }

        public SqMetadataRef NodeReference => new SqMetadataRef(DirBlockStart, BlockOffset);
    }
}
