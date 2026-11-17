using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.SquashFs.Types.Nodes
{
    class BasicIPC : SqInode
    {
        /// <summary>
        /// True for the extended (LFIFO/LSOCKET) inode layout.
        /// </summary>
        internal readonly bool IsExtended;

        public BasicIPC(SqInodeType Type, uint Mode, uint User, uint Group, uint LinkCount, uint XAttrId = InvalidXAttr) : base(XAttrId != InvalidXAttr ? 0x18 : 0x14)
        {
            IsExtended = XAttrId != InvalidXAttr;

            if (IsExtended)
            {
                if (Type == SqInodeType.BasicFifo)
                    Type = SqInodeType.ExtendedFifo;
                else if (Type == SqInodeType.BasicSocket)
                    Type = SqInodeType.ExtendedSocket;
            }

            InodeType = Type;
            Permissions = Mode;
            GidIndex = Group;
            UidIndex = User;

            HardLinkCount = LinkCount;

            if (IsExtended)
                XAttrIndex = XAttrId;
        }

        public BasicIPC(byte[] Data, bool IsExtended = false) : base(Data, IsExtended ? 0x18 : 0x14)
        {
            this.IsExtended = IsExtended;
        }

        /// <summary>
        /// INode size
        /// </summary>
        internal override long INodeSize => IsExtended ? 0x18 : 0x14;

        /// <summary>
        /// Index in the xattr id lookup table (extended inodes only).
        /// u32 xattr (0x14)
        /// </summary>
        internal override uint XAttrIndex
        {
            get { return IsExtended ? ReadUInt32(0x14) : InvalidXAttr; }
            set { if (IsExtended) WriteUInt32(0x14, value); }
        }

        /// <summary>
        /// The number of hard links to this ipc item
        /// u32 hard_link_count (0x10)
        /// </summary>
        public uint HardLinkCount
        {
            get { return ReadUInt32(0x10); }
            set { WriteUInt32(0x10, value); }
        }
    }
}
