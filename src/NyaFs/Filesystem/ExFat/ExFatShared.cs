using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.ExFat
{
    internal static class ExFatShared
    {
        public const int EntrySize = 32;
        public const int BytesPerSector = 512;

        public const byte EntryEnd = 0x00;
        public const byte EntryBitmap = 0x81;
        public const byte EntryUpcase = 0x82;
        public const byte EntryLabel = 0x83;
        public const byte EntryFile = 0x85;
        public const byte EntryStream = 0xC0;
        public const byte EntryName = 0xC1;

        public const ushort AttrDirectory = 0x10;
        public const ushort AttrArchive = 0x20;

        public static ushort ReadU16(byte[] Data, int Offset) =>
            (ushort)(Data[Offset] | (Data[Offset + 1] << 8));

        public static uint ReadU32(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8) |
                   (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));

        public static ulong ReadU64(byte[] Data, int Offset) =>
            ReadU32(Data, Offset) | ((ulong)ReadU32(Data, Offset + 4) << 32);

        public static void WriteU16(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
        }

        public static void WriteU32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }

        public static void WriteU64(byte[] Data, int Offset, ulong Value)
        {
            WriteU32(Data, Offset, (uint)Value);
            WriteU32(Data, Offset + 4, (uint)(Value >> 32));
        }

        public static void WriteAscii(byte[] Data, int Offset, int Length, string Value)
        {
            var Bytes = Encoding.ASCII.GetBytes(Value ?? "");
            Array.Clear(Data, Offset, Length);
            Array.Copy(Bytes, 0, Data, Offset, Math.Min(Bytes.Length, Length));
        }

        public static int AlignUp(int Value, int Alignment) =>
            ((Value + Alignment - 1) / Alignment) * Alignment;

        public static bool IsExFat(byte[] Data, int Offset = 0)
        {
            if (Data.Length < Offset + 512)
                return false;

            return (Data[Offset + 3] == (byte)'E') && (Data[Offset + 4] == (byte)'X') &&
                   (Data[Offset + 5] == (byte)'F') && (Data[Offset + 6] == (byte)'A') &&
                   (Data[Offset + 7] == (byte)'T') && (Data[Offset + 8] == (byte)' ') &&
                   (Data[Offset + 9] == (byte)' ') && (Data[Offset + 10] == (byte)' ') &&
                   (Data[Offset + 510] == 0x55) && (Data[Offset + 511] == 0xAA);
        }

        public static ushort[] DecompressUpcase(byte[] Data)
        {
            var Result = new ushort[65536];
            for (int i = 0; i < Result.Length; i++)
                Result[i] = (ushort)i;

            int Input = Data.Length / 2;
            int Oi = 0;
            for (int Si = 0; (Si < Input) && (Oi < Result.Length); Si++)
            {
                ushort Value = ReadU16(Data, Si * 2);
                if ((Value == 0xFFFF) && (Si + 1 < Input))
                {
                    Oi += ReadU16(Data, ++Si * 2);
                }
                else
                {
                    Result[Oi++] = Value;
                }
            }

            return Result;
        }

        public static ushort[] BuildUpcaseTable()
        {
            var Result = new ushort[65536];
            for (int i = 0; i < Result.Length; i++)
                Result[i] = (ushort)Char.ToUpperInvariant((char)i);

            return Result;
        }

        public static byte[] UpcaseToBytes(ushort[] Table)
        {
            var Result = new byte[Table.Length * 2];
            for (int i = 0; i < Table.Length; i++)
                WriteU16(Result, i * 2, Table[i]);
            return Result;
        }

        public static uint UpcaseChecksum(byte[] Table)
        {
            uint Result = 0;
            foreach (var B in Table)
            {
                Result = ((Result << 31) | (Result >> 1)) + B;
            }
            return Result;
        }

        public static ushort NameHash(string Name, ushort[] Upcase)
        {
            ushort Result = 0;
            foreach (var Ch in Name ?? "")
            {
                ushort C = Upcase[Ch & 0xFFFF];
                Result = (ushort)(((Result << 15) | (Result >> 1)) + (C & 0xFF));
                Result = (ushort)(((Result << 15) | (Result >> 1)) + (C >> 8));
            }
            return Result;
        }

        public static ushort EntrySetChecksum(byte[] Entries)
        {
            ushort Result = 0;
            for (int i = 0; i < Entries.Length; i++)
            {
                if ((i == 2) || (i == 3))
                    continue;

                Result = (ushort)(((Result << 15) | (Result >> 1)) + Entries[i]);
            }
            return Result;
        }

        public static uint BootChecksum(byte[] Data, int Offset)
        {
            uint Result = 0;
            for (int i = 0; i < 11 * BytesPerSector; i++)
            {
                if ((i == 106) || (i == 107) || (i == 112))
                    continue;

                Result = ((Result << 31) | (Result >> 1)) + Data[Offset + i];
            }
            return Result;
        }

        public static uint DosDateTime(DateTime Time)
        {
            var Local = Time.ToLocalTime();
            uint Year = (uint)Math.Max(0, Math.Min(127, Local.Year - 1980));
            uint Month = (uint)Local.Month;
            uint Day = (uint)Local.Day;
            uint Hour = (uint)Local.Hour;
            uint Minute = (uint)Local.Minute;
            uint Second = (uint)(Local.Second / 2);
            return (Year << 25) | (Month << 21) | (Day << 16) | (Hour << 11) | (Minute << 5) | Second;
        }

        public static DateTime ReadDosDateTime(uint Value)
        {
            int Year = 1980 + (int)((Value >> 25) & 0x7F);
            int Month = (int)((Value >> 21) & 0x0F);
            int Day = (int)((Value >> 16) & 0x1F);
            int Hour = (int)((Value >> 11) & 0x1F);
            int Minute = (int)((Value >> 5) & 0x3F);
            int Second = (int)((Value & 0x1F) * 2);

            try
            {
                return new DateTime(Year, Month, Day, Hour, Minute, Second, DateTimeKind.Local);
            }
            catch
            {
                return DateTime.Now;
            }
        }

        public static string ReadName(byte[] Entries, int Offset, int EntryCount, int Length)
        {
            var Builder = new StringBuilder();
            for (int i = 0; i < EntryCount; i++)
            {
                int Base = Offset + i * EntrySize;
                for (int C = 0; C < 15; C++)
                {
                    if (Builder.Length >= Length)
                        return Builder.ToString();

                    Builder.Append((char)ReadU16(Entries, Base + 2 + C * 2));
                }
            }

            if (Builder.Length > Length)
                Builder.Length = Length;

            return Builder.ToString();
        }

        public static void WriteName(List<byte> Entries, string Name)
        {
            int Count = (Name.Length + 14) / 15;
            for (int E = 0; E < Count; E++)
            {
                var Entry = new byte[EntrySize];
                Entry[0] = EntryName;
                for (int C = 0; C < 15; C++)
                {
                    int Index = E * 15 + C;
                    if (Index < Name.Length)
                        WriteU16(Entry, 2 + C * 2, Name[Index]);
                }
                Entries.AddRange(Entry);
            }
        }
    }
}
