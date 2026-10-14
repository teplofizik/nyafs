using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.UbiFs.Types
{
    internal class VolumeTableRecord : ArrayWrapper
    {
        public const int Size = 0xAC;

        public VolumeTableRecord(byte[] Data, long Offset) : base(Data, Offset, Size)
        {

        }

        /// <summary>
        /// How many physical eraseblocks are reserved for this volume
        /// u32 reserved_pebs (0x00)
        /// </summary>
        public uint ReservedPebs
        {
            get { return ReadUInt32BE(0x00); }
            set { WriteUInt32BE(0x00, value); }
        }

        /// <summary>
        /// Volume alignment
        /// u32 alignment (0x04)
        /// </summary>
        public uint Alignment
        {
            get { return ReadUInt32BE(0x04); }
            set { WriteUInt32BE(0x04, value); }
        }

        /// <summary>
        /// How many bytes are unused at the end of the each physical eraseblock to satisfy the requested alignment
        /// u32 data_pad (0x08)
        /// </summary>
        public uint DataPadding
        {
            get { return ReadUInt32BE(0x08); }
            set { WriteUInt32BE(0x08, value); }
        }

        /// <summary>
        /// volume type (%UBI_DYNAMIC_VOLUME or %UBI_STATIC_VOLUME)
        /// u8 vol_type (0x0C)
        /// </summary>
        public uint VolumeType
        {
            get { return ReadByte(0x0C); }
            set { WriteByte(0x0C, value); }
        }

        /// <summary>
        /// if volume update was started but not finished
        /// u8 upd_marker (0x0D)
        /// </summary>
        public uint UpdateMarker
        {
            get { return ReadByte(0x0D); }
            set { WriteByte(0x0D, value); }
        }

        /// <summary>
        /// volume name length
        /// u16 name_len (0x0E)
        /// </summary>
        public uint NameLength
        {
            get { return ReadUInt16BE(0x0E); }
            set { WriteUInt16BE(0x0E, value); }
        }

        public string Name
        {
            get { return ReadString(0x10, NameLength); }
            set { 
                WriteString(0x10, value, 0x80);
                NameLength = Convert.ToUInt32(value.Length);
            }
        }

        /// <summary>
        /// volume flags (%UBI_VTBL_AUTORESIZE_FLG)
        /// u8 flags (0x90)
        /// </summary>
        public uint Flags
        {
            get { return ReadByte(0x90); }
            set { WriteByte(0x90, value); }
        }

        // Padding 23

        /// <summary>
        /// u32 crc (0xA8)
        /// </summary>
        public uint Crc
        {
            get { return ReadUInt32BE(0xA8); }
            set { WriteUInt32BE(0xA8, value); }
        }

        /// <summary>
        /// Header crc
        /// </summary>
        public uint HeaderCrcCalculated => Crc32.CalcCrc(ReadArray(0x00, 0xA8));

        /// <summary>
        /// Корректная ли контрольная сумма
        /// </summary>
        public bool IsCorrect => HeaderCrcCalculated == Crc;

    }
}
