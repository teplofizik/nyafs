using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.UbiFs.Types
{
    internal class EraseCounterHeader : ArrayWrapper
    {
        public EraseCounterHeader(byte[] Data, long Offset, int Size) : base(Data, Offset, Size)
        {

        }

        /// <summary>
        /// erase counter header magic number (%UBI_EC_HDR_MAGIC)
        /// u32 magic (0x00)
        /// </summary>
        public uint Magic
        {
            get { return ReadUInt32BE(0x00); }
            set { WriteUInt32BE(0x00, value); }
        }

        /// <summary>
        /// version of UBI implementation which is supposed to accept this UBI image
        /// u8 version (0x04)
        /// </summary>
        public uint Version
        {
            get { return ReadByte(0x04); }
            set { WriteByte(0x04, value); }
        }

        // Padding 3 bytes

        /// <summary>
        /// the erase counter
        /// u64 ec (0x08)
        /// </summary>
        public ulong EraseCounter
        {
            get { return ReadUInt64BE(0x08); }
            set { WriteUInt64BE(0x08, value); }
        }

        /// <summary>
        /// Where the VID header starts
        /// u32 vid_hdr_offset (0x10)
        /// </summary>
        public uint VidHeaderOffset
        {
            get { return ReadUInt32BE(0x10); }
            set { WriteUInt32BE(0x10, value); }
        }

        /// <summary>
        /// Where the user data start
        /// u32 data_offset (0x14)
        /// </summary>
        public uint DataSize
        {
            get { return ReadUInt32BE(0x14); }
            set { WriteUInt32BE(0x14, value); }
        }

        /// <summary>
        /// Image sequence number
        /// u32 image_seq (0x18)
        /// </summary>
        public uint ImageSequence
        {
            get { return ReadUInt32BE(0x18); }
            set { WriteUInt32BE(0x18, value); }
        }

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
        public bool IsMagicCorrect => (Magic == 0x55424923u);

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
