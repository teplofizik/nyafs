using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.SquashFs.Types.Nodes
{
    class BasicDevice : SqInode
    {
        /// <summary>
        /// True for the extended (LBLKDEV/LCHRDEV) inode layout.
        /// </summary>
        internal readonly bool IsExtended;

        public BasicDevice(SqInodeType Type, uint Mode, uint User, uint Group, uint Major, uint Minor, uint XAttrId = InvalidXAttr) : base(XAttrId != InvalidXAttr ? 0x1C : 0x18)
        {
            IsExtended = XAttrId != InvalidXAttr;

            if (IsExtended)
            {
                if (Type == SqInodeType.BasicBlockDevice)
                    Type = SqInodeType.ExtendedBlockDevice;
                else if (Type == SqInodeType.BasicCharDevice)
                    Type = SqInodeType.ExtendedCharDevice;
            }

            InodeType = Type;
            Permissions = Mode;
            GidIndex = Group;
            UidIndex = User;

            HardLinkCount = 1;

            uint DevMajor = (Major & 0xfff) << 8;
            uint DevMinor = (Minor & 0xff) | (Minor & 0xfff00) << 12;

            Device = DevMajor | DevMinor;

            if (IsExtended)
                XAttrIndex = XAttrId;
        }

        public BasicDevice(byte[] Data, bool IsExtended = false) : base(Data, IsExtended ? 0x1C : 0x18)
        {
            this.IsExtended = IsExtended;
        }

        /// <summary>
        /// INode size
        /// </summary>
        internal override long INodeSize => IsExtended ? 0x1C : 0x18;

        /// <summary>
        /// Index in the xattr id lookup table (extended inodes only).
        /// u32 xattr (0x18)
        /// </summary>
        internal override uint XAttrIndex
        {
            get { return IsExtended ? ReadUInt32(0x18) : InvalidXAttr; }
            set { if (IsExtended) WriteUInt32(0x18, value); }
        }

        /// <summary>
        /// The number of hard links to this device
        /// u32 hard_link_count (0x10)
        /// </summary>
        public uint HardLinkCount
        {
            get { return ReadUInt32(0x10); }
            set { WriteUInt32(0x10, value); }
        }

        /// <summary>
        /// To extract the major device number, (device & 0xfff00) >> 8. To extract the minor device number, use (dev & 0xff) | ((dev >> 12) & 0xfff00)
        /// u32 device (0x14)
        /// </summary>
        public uint Device
        {
            get { return ReadUInt32(0x14); }
            set { WriteUInt32(0x14, value); }
        }

        /// <summary>
        /// Major device number
        /// </summary>
        public uint Major => (Device & 0xfff00) >> 8;

        /// <summary>
        /// Minor device number
        /// </summary>
        public uint Minor => (Device & 0xff) | ((Device >> 12) & 0xfff00);
    }
}
