using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Iso9660
{
    internal static class Iso9660Shared
    {
        public const int SectorSize = 2048;
        public const int PrimaryVolumeDescriptorSector = 16;
        public const int RootRecordOffset = 156;
        public const int DirectoryRecordFixedSize = 33;

        public static ushort ReadU16LE(byte[] Data, int Offset) =>
            (ushort)(Data[Offset] | (Data[Offset + 1] << 8));

        public static ushort ReadU16BE(byte[] Data, int Offset) =>
            (ushort)((Data[Offset] << 8) | Data[Offset + 1]);

        public static uint ReadU32LE(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8) |
                   (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));

        public static uint ReadU32BE(byte[] Data, int Offset) =>
            (uint)((Data[Offset] << 24) | (Data[Offset + 1] << 16) |
                   (Data[Offset + 2] << 8) | Data[Offset + 3]);

        public static void WriteU16LE(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
        }

        public static void WriteU16BE(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)(Value >> 8);
            Data[Offset + 1] = (byte)Value;
        }

        public static void WriteU32LE(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }

        public static void WriteU32BE(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)(Value >> 24);
            Data[Offset + 1] = (byte)(Value >> 16);
            Data[Offset + 2] = (byte)(Value >> 8);
            Data[Offset + 3] = (byte)Value;
        }

        public static void WriteBoth16(byte[] Data, int Offset, uint Value)
        {
            WriteU16LE(Data, Offset, Value);
            WriteU16BE(Data, Offset + 2, Value);
        }

        public static void WriteBoth32(byte[] Data, int Offset, uint Value)
        {
            WriteU32LE(Data, Offset, Value);
            WriteU32BE(Data, Offset + 4, Value);
        }

        public static string Normalize(string Path)
        {
            if (String.IsNullOrEmpty(Path) || (Path == ".") || (Path == "/"))
                return "";

            return Path.Replace('\\', '/').Trim('/');
        }

        public static string Parent(string Path)
        {
            int Slash = Path.LastIndexOf('/');
            return (Slash < 0) ? "" : Path.Substring(0, Slash);
        }

        public static string Name(string Path)
        {
            int Slash = Path.LastIndexOf('/');
            return (Slash < 0) ? Path : Path.Substring(Slash + 1);
        }

        public static string Combine(string Parent, string Name) =>
            String.IsNullOrEmpty(Parent) ? Name : Parent + "/" + Name;

        public static DateTime ReadRecordingDate(byte[] Data, int Offset)
        {
            int Year = Data[Offset] + 1900;
            int Month = Data[Offset + 1];
            int Day = Data[Offset + 2];
            int Hour = Data[Offset + 3];
            int Minute = Data[Offset + 4];
            int Second = Data[Offset + 5];
            int Offset15 = (sbyte)Data[Offset + 6];

            if ((Month < 1) || (Month > 12) || (Day < 1) || (Day > 31) ||
                (Hour > 23) || (Minute > 59) || (Second > 59))
                return DateTime.UtcNow;

            try
            {
                var Result = new DateTime(Year, Month, Day, Hour, Minute, Math.Min(Second, 59), DateTimeKind.Utc);
                return Result.AddMinutes(-Offset15 * 15);
            }
            catch
            {
                return DateTime.UtcNow;
            }
        }

        public static void WriteRecordingDate(byte[] Data, int Offset, DateTime Time)
        {
            var Utc = Time.ToUniversalTime();
            Data[Offset] = (byte)(Utc.Year - 1900);
            Data[Offset + 1] = (byte)Utc.Month;
            Data[Offset + 2] = (byte)Utc.Day;
            Data[Offset + 3] = (byte)Utc.Hour;
            Data[Offset + 4] = (byte)Utc.Minute;
            Data[Offset + 5] = (byte)Utc.Second;
            Data[Offset + 6] = 0;
        }

        public static string ReadAscii(byte[] Data, int Offset, int Length)
        {
            int End = Offset;
            while ((End < Offset + Length) && (Data[End] != 0))
                End++;

            return Encoding.ASCII.GetString(Data, Offset, End - Offset).Trim();
        }

        public static void WriteAscii(byte[] Data, int Offset, int Length, string Value)
        {
            var Bytes = Encoding.ASCII.GetBytes(Value ?? "");
            Array.Clear(Data, Offset, Length);
            Array.Copy(Bytes, 0, Data, Offset, Math.Min(Bytes.Length, Length));
        }

        public static string ReadJolietName(byte[] Data, int Offset, int Length)
        {
            if ((Length & 1) != 0)
                Length--;

            var Bytes = new byte[Length];
            Array.Copy(Data, Offset, Bytes, 0, Length);

            for (int i = 0; i < Bytes.Length; i += 2)
            {
                byte Tmp = Bytes[i];
                Bytes[i] = Bytes[i + 1];
                Bytes[i + 1] = Tmp;
            }

            return Encoding.Unicode.GetString(Bytes).TrimEnd('\0');
        }

        public static string IsoNameToDisplay(string Name)
        {
            var Result = Name ?? "";
            int Semicolon = Result.IndexOf(';');
            if (Semicolon >= 0)
                Result = Result.Substring(0, Semicolon);

            if (Result.EndsWith(".", StringComparison.Ordinal))
                Result = Result.Substring(0, Result.Length - 1);

            return Result;
        }
    }
}
