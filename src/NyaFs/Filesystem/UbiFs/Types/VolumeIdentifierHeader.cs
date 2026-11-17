using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.UbiFs.Types
{
    public enum VolumeType
    {
        UBI_VID_DYNAMIC = 1,
        UBI_VID_STATIC = 2
    }

    public enum VolumeCompat
    {
        UBI_COMPAT_NONE = 0,
        UBI_COMPAT_DELETE = 1,   // delete this internal volume before anything is written to the flash
        UBI_COMPAT_RO = 2,       // attach this device in read-only mode
        UBI_COMPAT_PRESERVE = 4, // preserve this internal volume - do not touch its physical eraseblocks, don't allow the wear-leveling sub-system to move them
        UBI_COMPAT_REJECT = 5    // reject this UBI image
    };

    internal class VolumeIdentifierHeader : ArrayWrapper
    {
        public VolumeIdentifierHeader(byte[] Data, long Offset, int Size) : base(Data, Offset, Size)
        {

        }

        /// <summary>
        /// Must match the value of 0x55424923 to be considered a UBIFS image
        /// u32 magic (0x00)
        /// </summary>
        public uint Magic
        {
            get { return ReadUInt32BE(0x00); }
            set { WriteUInt32BE(0x00, value); }
        }

        /// <summary>
        /// UBI implementation version which is supposed to accept this UBI image(%UBI_VERSION)
        /// u8 version (0x04)
        /// </summary>
        public uint Version
        {
            get { return ReadByte(0x04); }
            set { WriteByte(0x04, value); }
        }

        /// <summary>
        /// Version of volume type (UBI_VID_DYNAMIC=1 or UBI_VID_STATIC=2)
        /// u8 vol_type (0x05)
        /// </summary>
        public VolumeType VolumeType
        {
            get { return (VolumeType)ReadByte(0x05); }
            set { WriteByte(0x05, Convert.ToInt32(value)); }
        }

        /// <summary>
        /// If this logical eraseblock was copied from another physical eraseblock (for wear-leveling reasons)
        /// u8 copy_flag (0x06)
        /// </summary>
        public uint CopyFlag
        {
            get { return ReadByte(0x06); }
            set { WriteByte(0x06, value); }
        }

        /// <summary>
        /// Compatibility of this volume (%0, %UBI_COMPAT_DELETE, %UBI_COMPAT_IGNORE, %UBI_COMPAT_PRESERVE, or %UBI_COMPAT_REJECT)
        /// u8 compat (0x07)
        /// </summary>
        public VolumeCompat Compat
        {
            get { return (VolumeCompat)ReadByte(0x07); }
            set { WriteByte(0x07, Convert.ToInt32(value)); }
        }

        /// <summary>
        /// ID of this volume
        /// u32 vol_id (0x08)
        /// </summary>
        public uint VolumeId
        {
            get { return ReadUInt32BE(0x08); }
            set { WriteUInt32BE(0x08, value); }
        }

        /// <summary>
        /// Logical eraseblock number
        /// u32 lnum (0x0C)
        /// </summary>
        public uint LNum
        {
            get { return ReadUInt32BE(0x0C); }
            set { WriteUInt32BE(0x0C, value); }
        }

        // Padding 4 bytes ...

        /// <summary>
        /// How many bytes of data this logical eraseblock contains
        /// u32 data_size (0x14)
        /// </summary>
        public uint DataSize
        {
            get { return ReadUInt32BE(0x14); }
            set { WriteUInt32BE(0x14, value); }
        }

        /// <summary>
        /// Total number of used logical eraseblocks in this volume
        /// u32 used_ebs (0x18)
        /// </summary>
        public uint UsedEBs
        {
            get { return ReadUInt32BE(0x18); }
            set { WriteUInt32BE(0x18, value); }
        }

        /// <summary>
        /// How many bytes at the end of this physical eraseblock are not used
        /// u32 data_pad (0x1C)
        /// </summary>
        public uint DataPadding
        {
            get { return ReadUInt32BE(0x1C); }
            set { WriteUInt32BE(0x1C, value); }
        }

        /// <summary>
        /// CRC checksum of the data stored in this logical eraseblock
        /// u32 data_crc (0x20)
        /// </summary>
        public uint DataCrc
        {
            get { return ReadUInt32BE(0x20); }
            set { WriteUInt32BE(0x20, value); }
        }

        // Padding 4 bytes ...

        /// <summary>
        /// Sequence number
        /// u64 sqnum (0x28)
        /// </summary>
        public ulong SQNum
        {
            get { return ReadUInt64BE(0x28); }
            set { WriteUInt64BE(0x28, value); }
        }

        // Padding 12 bytes ...

        /// <summary>
        /// Header crc
        /// u32 hdr_crc (0x20)
        /// </summary>
        public uint HeaderCrc
        {
            get { return ReadUInt32BE(0x3C); }
            set { WriteUInt32BE(0x3C, value); }
        }
        /// <summary>
        /// Header crc
        /// </summary>
        public uint HeaderCrcCalculated => Crc32.CalcCrc(ReadArray(0x00, 0x3C));

        /// <summary>
        /// Is magic field correct
        /// </summary>
        public bool IsMagicCorrect => (Magic == 0x55424921u);

        /// <summary>
        /// Корректная ли контрольная сумма
        /// </summary>
        public bool IsChecksumCorrect => HeaderCrcCalculated == HeaderCrc;

        /// <summary>
        /// Is block after header empty
        /// </summary>
        public bool IsOtherPartEmpty
        {
            get
            {
                for(int i = 0x40; i < getLength(); i += 0x04)
                {
                    if(ReadUInt32(i) != 0xFFFFFFFFu)
                        return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Is header correct
        /// </summary>
        public bool IsCorrect => IsMagicCorrect && IsOtherPartEmpty && IsChecksumCorrect;
    }
}
