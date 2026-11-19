using CrcSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NyaFs.Filesystem.CramFs
{
    static class Crc32
    {
        public static uint CalcCrc(byte[] data)
        {
            var crc32 = new Crc(new CrcParameters(32, 0x04c11db7, 0xffffffff, 0xffffffff, true, true));

            return Convert.ToUInt32(crc32.CalculateAsNumeric(data));
        }

        public static uint Compute(Stream Data, long Length)
        {
            var Table = CreateTable();
            var Crc = 0xFFFFFFFFu;
            var Buffer = new byte[64 * 1024];
            var Position = Data.Position;
            Data.Seek(0, SeekOrigin.Begin);

            while (Length > 0)
            {
                var Read = Data.Read(Buffer, 0, (int)Math.Min(Buffer.Length, Length));
                if (Read <= 0)
                    break;

                for (var I = 0; I < Read; I++)
                    Crc = (Crc >> 8) ^ Table[(Crc ^ Buffer[I]) & 0xFF];

                Length -= Read;
            }

            Data.Seek(Position, SeekOrigin.Begin);
            return Crc ^ 0xFFFFFFFF;
        }

        private static uint[] CreateTable()
        {
            var Table = new uint[256];
            for (uint I = 0; I < 256; I++)
            {
                var C = I;
                for (var K = 0; K < 8; K++)
                    C = ((C & 1) != 0) ? (0xEDB88320u ^ (C >> 1)) : (C >> 1);
                Table[I] = C;
            }
            return Table;
        }
    }
}
