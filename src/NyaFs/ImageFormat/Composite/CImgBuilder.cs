using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Builds the CVITEK CIMG container format used by raw2cimg.py.
    /// </summary>
    public sealed class CImgBuilder
    {
        public string Label = "gpt";
        public int ChunkSize = 16 * 1024 * 1024;
        public uint Offset = 0;
        public uint PartSize = 0;

        public byte[] Build(byte[] Payload)
        {
            if (Payload == null)
                Payload = Array.Empty<byte>();
            if (ChunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(ChunkSize));

            int Chunks = Math.Max(1, (Payload.Length + ChunkSize - 1) / ChunkSize);
            int FileSize = Payload.Length + Chunks * 0x40;
            var Result = new byte[0x40 + FileSize];

            WriteU32(Result, 0x00, 0x474D4943u); // CIMG
            WriteU32(Result, 0x04, 1);
            WriteU32(Result, 0x08, 0x40);
            WriteU32(Result, 0x0C, (uint)Chunks);
            WriteU32(Result, 0x10, (uint)FileSize);
            WriteString(Result, 0x14, 32, Label);

            int Source = 0;
            int Target = 0x40;
            uint CurrentOffset = Offset;
            for (int i = 0; i < Chunks; i++)
            {
                int Size = Math.Min(ChunkSize, Payload.Length - Source);
                if (Size < 0)
                    Size = 0;

                var Chunk = new byte[Size];
                Array.Copy(Payload, Source, Chunk, 0, Size);

                WriteU32(Result, Target + 0x00, 1); // CHUNK_TYPE_CRC_CHECK
                WriteU32(Result, Target + 0x04, (uint)Size);
                WriteU32(Result, Target + 0x08, CurrentOffset);
                WriteU32(Result, Target + 0x0C, PartSize > 0 ? PartSize : (uint)Payload.Length);
                WriteU32(Result, Target + 0x10, CalcCrc(Chunk));
                Array.Copy(Chunk, 0, Result, Target + 0x40, Size);

                Source += Size;
                CurrentOffset += (uint)Size;
                Target += 0x40 + Size;
            }

            return Result;
        }

        private static uint CalcCrc(byte[] Data)
        {
            var Crc32 = new CrcSharp.Crc(new CrcSharp.CrcParameters(32, 0x04c11db7, 0xffffffff, 0xffffffff, true, true));
            return Convert.ToUInt32(Crc32.CalculateAsNumeric(Data));
        }

        private static void WriteString(byte[] Data, int Offset, int Size, string Value)
        {
            var Bytes = Encoding.ASCII.GetBytes(Value ?? "");
            Array.Copy(Bytes, 0, Data, Offset, Math.Min(Size, Bytes.Length));
        }

        private static void WriteU32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }
    }

    public sealed class CImgWriter
    {
        private readonly string Filename;

        public CImgWriter(string Filename)
        {
            this.Filename = Filename;
        }

        public void Write(byte[] Payload) => System.IO.File.WriteAllBytes(Filename, new CImgBuilder().Build(Payload));

        public byte[] Build(byte[] Payload) => new CImgBuilder().Build(Payload);
    }
}
