using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Types
{
    /// <summary>
    /// CVITEK CIMG container.
    ///
    /// File header (64 bytes):
    ///   magic, version, chunk-header size, chunk count, payload+chunk-header size,
    ///   32-byte partition label, reserved.
    /// Each chunk has a 64-byte header (type, data size, program offset,
    /// partition size, CRC32, reserved) followed by its data.
    /// </summary>
    class CvImage : RawPacket
    {
        public const uint CIMG_MAGIC = 0x474D4943u;
        public const int FILE_HEADER_SIZE = 0x40;
        public const int CHUNK_HEADER_SIZE = 0x40;

        public CvImage(byte[] Data) : base(Data) { }
        public CvImage(string Filename) : base(System.IO.File.ReadAllBytes(Filename)) { }

        public CvImage(string Name, byte[] Data, UInt32 Flags) : base(FILE_HEADER_SIZE + CHUNK_HEADER_SIZE + Data.Length)
        {
            Magic = CIMG_MAGIC;
            Version = 1;
            HeaderSize = CHUNK_HEADER_SIZE;
            TotalChunk = 1;
            FileSize = (uint)(CHUNK_HEADER_SIZE + Data.Length);
            ImageName = Name;

            WriteUInt32(0x40, 1);                 // CHUNK_TYPE_CRC_CHECK
            WriteUInt32(0x44, (uint)Data.Length);
            WriteUInt32(0x48, Flags);
            WriteUInt32(0x4C, (uint)Data.Length);
            WriteUInt32(0x50, CalcCrc(Data));
            WriteArray(0x80, Data, Data.Length);
        }

        private static uint CalcCrc(byte[] Data)
        {
            if (Data == null)
                return 0;

            var Crc32 = new CrcSharp.Crc(new CrcSharp.CrcParameters(32, 0x04c11db7, 0xffffffff, 0xffffffff, true, true));
            return Convert.ToUInt32(Crc32.CalculateAsNumeric(Data));
        }

        /// <summary>New-style chunked CIMG (raw2cimg.py from cvi_rtt).</summary>
        private bool NewCorrect
        {
            get
            {
                if ((Magic != CIMG_MAGIC) || (HeaderSize < CHUNK_HEADER_SIZE) || (TotalChunk == 0))
                    return false;

                long Position = HeaderSize;
                long TotalData = 0;
                for (uint i = 0; i < TotalChunk; i++)
                {
                    if (Position + CHUNK_HEADER_SIZE > getLength())
                        return false;

                    uint Type = ReadUInt32((int)Position);
                    uint Size = ReadUInt32((int)Position + 4);
                    long DataOffset = Position + CHUNK_HEADER_SIZE;
                    if (DataOffset + Size > getLength())
                        return false;

                    if (Type == 1)
                    {
                        uint StoredCrc = ReadUInt32((int)Position + 16);
                        var Chunk = ReadArray(DataOffset, Size);
                        if (StoredCrc != CalcCrc(Chunk))
                            return false;
                    }

                    TotalData += Size;
                    Position = DataOffset + Size;
                }

                return (FileSize >= TotalData) && (FileSize <= getLength());
            }
        }

        /// <summary>
        /// Legacy single-payload CIMG: at 0x40 there is one image header, and
        /// the CRC is stored at 0x4C instead of the new chunk header's +0x10.
        /// </summary>
        private bool LegacyCorrect
        {
            get
            {
                if ((Magic != CIMG_MAGIC) || (HeaderSize < FILE_HEADER_SIZE) || (getLength() < 0x80))
                    return false;

                uint ImageSize = ReadUInt32(0x44);
                uint ContentSize = ReadUInt32(0x10);
                if ((ImageSize == 0) || (ImageSize + FILE_HEADER_SIZE != ContentSize) ||
                    (ContentSize + HeaderSize > getLength()))
                    return false;

                var Chunk = ReadArray(0x80, ImageSize);
                return ReadUInt32(0x4C) == CalcCrc(Chunk);
            }
        }

        /// <summary>True when either the current or legacy CIMG layout is valid.</summary>
        public bool Correct => NewCorrect || LegacyCorrect;

        /// <summary>Concatenated payload of all chunks.</summary>
        public byte[] Content
        {
            get
            {
                if (NewCorrect)
                {
                    var Result = new byte[(int)FileSize - (long)TotalChunk * CHUNK_HEADER_SIZE];
                    int Output = 0;
                    long Position = HeaderSize;
                    for (uint i = 0; i < TotalChunk; i++)
                    {
                        uint Size = ReadUInt32((int)Position + 4);
                        long DataOffset = Position + CHUNK_HEADER_SIZE;
                        Array.Copy(ReadArray(DataOffset, Size), 0, Result, Output, (int)Size);
                        Output += (int)Size;
                        Position = DataOffset + Size;
                    }
                    return Result;
                }

                if (LegacyCorrect)
                    return ReadArray(0x80, ReadUInt32(0x44));

                return null;
            }
        }

        public uint Magic
        {
            get { return ReadUInt32(0x00); }
            set { WriteUInt32(0x00, value); }
        }

        public uint Version
        {
            get { return ReadUInt32(0x04); }
            set { WriteUInt32(0x04, value); }
        }

        public uint HeaderSize
        {
            get { return ReadUInt32(0x08); }
            set { WriteUInt32(0x08, value); }
        }

        public uint TotalChunk
        {
            get { return ReadUInt32(0x0C); }
            set { WriteUInt32(0x0C, value); }
        }

        /// <summary>Size of all chunk headers plus their payloads (excluding the 64-byte file header).</summary>
        public uint FileSize
        {
            get { return ReadUInt32(0x10); }
            set { WriteUInt32(0x10, value); }
        }

        public string ImageName
        {
            get { return ReadANSIString(0x14, 0x20); }
            set { WriteANSIString(0x14, value, 0x20); }
        }

        public uint ChunkType => ReadUInt32(0x40);
        public uint ImageSize
        {
            get
            {
                if (!NewCorrect)
                    return ReadUInt32(0x44);

                uint Result = 0;
                long Position = HeaderSize;
                for (uint i = 0; i < TotalChunk; i++)
                {
                    uint Size = ReadUInt32((int)Position + 4);
                    Result += Size;
                    Position += CHUNK_HEADER_SIZE + Size;
                }
                return Result;
            }
        }

        public uint CRC32 => NewCorrect ? ReadUInt32(0x50) : ReadUInt32(0x4C);
        public uint CalculatedCRC32
        {
            get
            {
                var Data = Content;
                return (Data != null) ? CalcCrc(Data) : 0;
            }
        }
    }
}
