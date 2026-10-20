using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.SquashFs.Types.Nodes
{
    class BasicSymLink : SqInode
    {
        /// <summary>
        /// True for the extended (LSYMLINK) inode layout.
        /// </summary>
        internal readonly bool IsExtended;

        private static int CalcNodeLength(string Target, bool IsExtended = false) => 0x18 + Target.Length + (IsExtended ? 4 : 0);

        public BasicSymLink(uint Mode, uint User, uint Group, uint HardLinkCount, string Target, uint XAttrId = InvalidXAttr) : base(CalcNodeLength(Target, XAttrId != InvalidXAttr))
        {
            IsExtended = XAttrId != InvalidXAttr;

            InodeType = IsExtended ? SqInodeType.ExtendedSymlink : SqInodeType.BasicSymlink;
            Permissions = Mode;
            GidIndex = Group;
            UidIndex = User;
            this.HardLinkCount = HardLinkCount;

            TargetSize = Convert.ToUInt32(Target.Length);
            WriteString(0x18, Target, Target.Length);

            if (IsExtended)
                XAttrIndex = XAttrId;
        }

        public BasicSymLink(byte[] Data, bool IsExtended = false) : base(Data, 0x18 + Data.ReadUInt32(0x14) + (IsExtended ? 4 : 0))
        {
            this.IsExtended = IsExtended;
        }

        /// <summary>
        /// INode size
        /// </summary>
        internal override long INodeSize => 0x18 + TargetSize + (IsExtended ? 4 : 0);

        /// <summary>
        /// Index in the xattr id lookup table (extended inodes only).
        /// u32 xattr (0x18 + target_size)
        /// </summary>
        internal override uint XAttrIndex
        {
            get { return IsExtended ? ReadUInt32(0x18 + TargetSize) : InvalidXAttr; }
            set { if (IsExtended) WriteUInt32(0x18 + TargetSize, value); }
        }

        /// <summary>
        /// The number of hard links to this symlink
        /// u32 hard_link_count (0x10)
        /// </summary>
        public uint HardLinkCount
        {
            get { return ReadUInt32(0x10); }
            set { WriteUInt32(0x10, value); }
        }


        /// <summary>
        /// The size in bytes of the target_path this symlink points to
        /// u32 target_size (0x14)
        /// </summary>
        public uint TargetSize
        {
            get { return ReadUInt32(0x14); }
            set { WriteUInt32(0x14, value); }
        }

        /// <summary>
        /// The index of a fragment entry in the fragment table which describes the data block the fragment of this file is stored in. 
        /// If this file does not end with a fragment, this should be 0xFFFFFFFF
        /// char target_path[target_size] (0x18)
        /// </summary>
        public byte[] TargetPath
        {
            get { return ReadArray(0x18, TargetSize); }
            set { WriteArray(0x18, value, TargetSize); }
        }

        public string Target => UTF8Encoding.UTF8.GetString(TargetPath);
    }
}
