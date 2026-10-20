using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.SquashFs.Types
{
    /// <summary>
    /// Entry of the xattr id lookup table.
    /// </summary>
    class SqXattrId : ArrayWrapper
    {
        public SqXattrId() : base(new byte[0x10], 0, 0x10)
        {
            Fill(0);
        }

        public SqXattrId(byte[] Data, long Offset) : base(Data, Offset, 0x10)
        {

        }

        /// <summary>
        /// Metadata reference to the first xattr entry of this id, relative to the xattr table start.
        /// u64 xattr (0x00)
        /// </summary>
        public ulong XAttr
        {
            get { return ReadUInt64(0x00); }
            set { WriteUInt64(0x00, value); }
        }

        /// <summary>
        /// Number of xattr entries.
        /// u32 count (0x08)
        /// </summary>
        public uint Count
        {
            get { return ReadUInt32(0x08); }
            set { WriteUInt32(0x08, value); }
        }

        /// <summary>
        /// Total uncompressed size of the name/value pairs.
        /// u32 size (0x0C)
        /// </summary>
        public uint Size
        {
            get { return ReadUInt32(0x0C); }
            set { WriteUInt32(0x0C, value); }
        }

        public long XAttrBlock => Convert.ToInt64(XAttr >> 16);

        public int XAttrOffset => Convert.ToInt32(XAttr & 0xFFFF);
    }
}
