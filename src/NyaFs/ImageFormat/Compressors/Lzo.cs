using System;
using NyaLZO;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Compressors
{
    public static class LZO
    {
        private static readonly byte[] Magic = new byte[] { 0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// Compress data into a minimal lzop container (LZO1X-1, Adler32 checksums,
        /// one block, no file name). The layout is compatible with the reader below.
        /// </summary>
        public static byte[] Compress(byte[] Data)
        {
            if (Data == null)
                throw new ArgumentNullException(nameof(Data));

            var Compressed = LZO1xCompressor.Compress(Data);
            var Header = new byte[50];

            Array.Copy(Magic, 0, Header, 0, Magic.Length);
            WriteBE16(Header, 0x09, 0x0940);       // version
            WriteBE16(Header, 0x0B, 0x20A0);       // lib version
            WriteBE16(Header, 0x0D, 0x0940);       // version needed
            Header[0x0F] = 1;                      // method: LZO1X-1
            Header[0x10] = 5;                      // level
            WriteBE32(Header, 0x11, 0x00000001);   // F_ADLER32_D (no compressed checksum)
            WriteBE32(Header, 0x15, 0);            // mode
            WriteBE32(Header, 0x19, 0);            // mtime
            WriteBE32(Header, 0x1D, 0);            // gmtdiff
            Header[0x21] = 0;                      // file name length

            // lzop validates an Adler32 checksum over the serialized header.
            WriteBE32(Header, 0x22, Adler32(Header, 9, 0x22 - 9));

            // One block: uncompressed size, compressed size, data checksum, data.
            WriteBE32(Header, 0x26, (uint)Data.Length);
            WriteBE32(Header, 0x2A, (uint)Compressed.Length);
            WriteBE32(Header, 0x2E, Adler32(Data));

            var Result = new byte[Header.Length + Compressed.Length + 4];
            Array.Copy(Header, 0, Result, 0, Header.Length);
            Array.Copy(Compressed, 0, Result, Header.Length, Compressed.Length);
            // Zero-length block terminator (lzop reads blocks until src_len == 0).
            return Result;
        }

        private static void WriteBE16(byte[] Data, int Offset, int Value)
        {
            Data[Offset] = (byte)(Value >> 8);
            Data[Offset + 1] = (byte)Value;
        }

        private static void WriteBE32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)(Value >> 24);
            Data[Offset + 1] = (byte)(Value >> 16);
            Data[Offset + 2] = (byte)(Value >> 8);
            Data[Offset + 3] = (byte)Value;
        }

        private static uint Adler32(byte[] Data, int Offset, int Length)
        {
            const uint Mod = 65521;
            uint A = 1;
            uint B = 0;
            for (var I = Offset; I < Offset + Length; I++)
            {
                A = (A + Data[I]) % Mod;
                B = (B + A) % Mod;
            }
            return (B << 16) | A;
        }

        private static uint Adler32(byte[] Data) => Adler32(Data, 0, Data.Length);

        public static byte[] Decompress(byte[] Data)
        {
            // Check header
            var Header = new LzoHeader(Data);
            if (Header.IsMagicCorrect)
            {
                return LZO1xDecompressor.Decompress(Header.PackedData);
            }
            else
                return null;
        }

        private class LzoHeader : RawPacket
        {
            private readonly byte[] CorrectMagic = new byte[] { 0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A };

            public LzoHeader(byte[] Raw) : base(Raw)
            {

            }

            /// <summary>
            /// Magic value
            /// 89 4C 5A 4F 00 0D 0A 1A 0A
            /// </summary>
            public byte[] Magic
            {
                get { return ReadArray(0, 9); }
                set { WriteArray(0, value, 9); }
            }

            public uint Version
            {
                get { return ReadUInt16BE(0x09); }
                set { WriteUInt16BE(0x09, value); }
            }

            public uint LibVersion
            {
                get { return ReadUInt16BE(0x0B); }
                set { WriteUInt16BE(0x0B, value); }
            }

            public uint VersionNeeded
            {
                get { return ReadUInt16BE(0x0D); }
                set { WriteUInt16BE(0x0D, value); }
            }

            public uint Method
            {
                get { return ReadByte(0x0F); }
                set { WriteByte(0x0f, Convert.ToByte(value)); }
            }

            private long FlagsOffset => (Version >= 0x940) ? 0x11 : 0x10;

            public uint Level
            {
                get { return (Version >= 0x940) ? ReadByte(0x10) : (byte)0; }
                set { if (Version >= 0x940) WriteByte(0x10, Convert.ToByte(value)); }
            }

            public LzoHeaderFlags Flags
            {
                get { return (LzoHeaderFlags)ReadUInt32BE(FlagsOffset); }
                set { WriteUInt32BE(FlagsOffset, Convert.ToUInt32(value)); }
            }

            /// <summary>
            /// Filter: 4 bytes(only if flags & F_H_FILTER)
            /// </summary>
            public uint Filter
            {
                get { return (Flags.HasFlag(LzoHeaderFlags.FILTER)) ? ReadUInt32(FlagsOffset + 0x04) : 0; }
                set { if (Flags.HasFlag(LzoHeaderFlags.FILTER)) WriteUInt32(FlagsOffset + 0x04, value); }
            }

            private long ModeOffset => Flags.HasFlag(LzoHeaderFlags.FILTER) ? FlagsOffset + 0x08 : FlagsOffset + 0x04;

            public uint Mode
            {
                get { return ReadUInt32BE(ModeOffset); }
                set { WriteUInt32BE(ModeOffset, value); }
            }

            /// <summary>
            /// Exists is (version >= 0x0940)
            /// </summary>
            public uint Mtime
            {
                get { return HasMTimeHigh ? ReadUInt32BE(ModeOffset + 0x04) : 0; }
                set { if(HasMTimeHigh) WriteUInt32BE(ModeOffset + 0x04, value); }
            }

            private bool HasMTimeHigh => (Version >= 0x0940);

            public uint GMTdiff
            {
                get { return HasMTimeHigh ? ReadUInt32BE(ModeOffset + 0x08) : 0; }
                set { if(HasMTimeHigh) WriteUInt32BE(ModeOffset + 0x08, value); }
            }

            private long FileNameLengthOffset => ModeOffset + (HasMTimeHigh ? 0x0cu : 0x08u);

            public uint FileNameLength
            {
                get { return ReadByte(FileNameLengthOffset); }
                set { WriteByte(FileNameLengthOffset, Convert.ToByte(value)); }
            }

            public string Name
            {
                get { return ReadString(FileNameLengthOffset + 0x01, FileNameLength); }
                set { WriteString(FileNameLengthOffset + 0x01, value, FileNameLength); }
            }

            private long ChecksumOffset => FileNameLengthOffset + 0x01 + FileNameLength;

            /// <summary>
            /// (CRC32 if flags & F_H_CRC32 else Adler32)
            /// </summary>
            public uint ChecksumOriginal
            {
                get { return ReadUInt32BE(ChecksumOffset); }
                set { WriteUInt32BE(ChecksumOffset, value); }
            }

            public uint UncompressedSize
            {
                get { return ReadUInt32BE(ChecksumOffset + 0x04); }
                set { WriteUInt32BE(ChecksumOffset + 0x04, value); }
            }

            public uint CompressedSize
            {
                get { return ReadUInt32BE(ChecksumOffset + 0x08); }
                set { WriteUInt32BE(ChecksumOffset + 0x08, value); }
            }

            public uint ChecksumUncompressed
            {
                get { return ReadUInt32BE(ChecksumOffset + 0x0c); }
                set { WriteUInt32BE(ChecksumOffset + 0x0c, value); }
            }

            /// <summary>
            /// (only if flags & F_ADLER32_C or flags & F_CRC32_C)
            /// </summary>
            public uint ChecksumCompressed
            {
                get { return ReadUInt32BE(ChecksumOffset + 0x10); }
                set { WriteUInt32BE(ChecksumOffset + 0x10, value); }
            }

            /*
             * Checksum, compressed data: 4 bytes(only if flags & F_ADLER32_C or flags & F_CRC32_C)
             * Compressed data: 0-xxx bytes
            */

            private long PacketDataOffset => ChecksumOffset + 0x10;
            public byte[] PackedData => ReadArray(PacketDataOffset, CompressedSize);

            public bool IsMagicCorrect
            {
                get
                {
                    for(int i = 0; i < CorrectMagic.Length; i++)
                    {
                        if (Magic[i] != CorrectMagic[i])
                            return false;
                    }
                    return true;
                }
            }

            [Flags]
            public enum LzoHeaderFlags
            {
                FILTER = 0x00000800
            }
        }
    }
}
