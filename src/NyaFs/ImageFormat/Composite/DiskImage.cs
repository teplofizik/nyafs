using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>One MBR or GPT partition inside a raw disk image.</summary>
    public sealed class DiskPartition
    {
        public int Index;
        public string Name = "";
        public string TypeGuid = "";
        public string PartitionGuid = "";
        public byte MbrType;
        public bool IsGpt;
        public long StartLba;
        public long SectorCount;

        public long Offset => StartLba * DiskImage.SectorSize;
        public long Size => SectorCount * DiskImage.SectorSize;

        public byte[] Read(byte[] Disk)
        {
            if ((Disk == null) || (Offset < 0) || (Size <= 0) || (Offset + Size > Disk.Length))
                return null;

            var Result = new byte[Size];
            Buffer.BlockCopy(Disk, (int)Offset, Result, 0, (int)Size);
            return Result;
        }
    }

    /// <summary>
    /// Reads MBR and GPT partition tables from a raw disk image without unpacking
    /// or interpreting the filesystems inside the partitions.
    /// </summary>
    public sealed class DiskImage
    {
        public const int SectorSize = 512;

        public byte[] Raw;
        public bool Correct;
        public bool IsGpt;
        public readonly List<DiskPartition> Partitions = new List<DiskPartition>();

        public static bool TryParse(byte[] Raw, out DiskImage Image)
        {
            Image = new DiskImage { Raw = Raw };
            if ((Raw == null) || (Raw.Length < SectorSize))
                return false;

            if ((Raw[510] != 0x55) || (Raw[511] != 0xAA))
                return false;

            var Type0 = Raw[446 + 4];
            if (Type0 == 0xEE)
            {
                if (TryParseGpt(Raw, Image))
                {
                    Image.IsGpt = true;
                    Image.Correct = true;
                    return true;
                }
            }

            if (TryParseMbr(Raw, Image))
            {
                Image.IsGpt = false;
                Image.Correct = true;
                return true;
            }

            return false;
        }

        private static bool TryParseMbr(byte[] Raw, DiskImage Image)
        {
            var DiskSectors = Raw.Length / SectorSize;
            var LogicalIndex = 0;
            for (var I = 0; I < 4; I++)
            {
                var Offset = 446 + I * 16;
                var Type = Raw[Offset + 4];
                var Start = ReadU32(Raw, Offset + 8);
                var Count = ReadU32(Raw, Offset + 12);
                if ((Type == 0) || (Start == 0) || (Count == 0))
                    continue;
                if ((Start + (long)Count) > DiskSectors)
                    continue;

                if (IsExtendedType(Type))
                {
                    ParseExtended(Raw, Image, Start, Count, ref LogicalIndex);
                    continue;
                }

                Image.Partitions.Add(new DiskPartition
                {
                    Index = I,
                    Name = "partition" + (I + 1),
                    MbrType = Type,
                    StartLba = Start,
                    SectorCount = Count
                });
            }
            return Image.Partitions.Count > 0;
        }

        private static bool IsExtendedType(byte Type) =>
            (Type == 0x05) || (Type == 0x0F) || (Type == 0x85);

        private static void ParseExtended(byte[] Raw, DiskImage Image, long ExtendedStart,
            long ExtendedCount, ref int LogicalIndex)
        {
            var DiskSectors = Raw.Length / SectorSize;
            var ExtendedEnd = ExtendedStart + ExtendedCount;
            var Visited = new HashSet<long>();
            var EbrLba = ExtendedStart;

            while ((EbrLba >= ExtendedStart) && (EbrLba < ExtendedEnd) &&
                (EbrLba + 1 < DiskSectors) && Visited.Add(EbrLba) && (LogicalIndex < 128))
            {
                var Base = (int)(EbrLba * SectorSize);
                if ((Raw[Base + 510] != 0x55) || (Raw[Base + 511] != 0xAA))
                    break;

                var Entry = Base + 446;
                var Type = Raw[Entry + 4];
                var Start = ReadU32(Raw, Entry + 8);
                var Count = ReadU32(Raw, Entry + 12);
                var LogicalStart = EbrLba + Start;
                if ((Type != 0) && (Start != 0) && (Count != 0) &&
                    (LogicalStart >= ExtendedStart) &&
                    (LogicalStart + (long)Count <= ExtendedEnd) &&
                    (LogicalStart + (long)Count <= DiskSectors))
                {
                    Image.Partitions.Add(new DiskPartition
                    {
                        Index = 4 + LogicalIndex,
                        Name = "partition" + (5 + LogicalIndex),
                        MbrType = Type,
                        StartLba = LogicalStart,
                        SectorCount = Count
                    });
                    LogicalIndex++;
                }

                var Next = Base + 446 + 16;
                var NextType = Raw[Next + 4];
                var NextStart = ReadU32(Raw, Next + 8);
                if (!IsExtendedType(NextType) || (NextStart == 0))
                    break;

                EbrLba = ExtendedStart + NextStart;
            }
        }

        private static bool TryParseGpt(byte[] Raw, DiskImage Image)
        {
            if (Raw.Length < 1024)
                return false;
            if (Raw[512] != (byte)'E' || Raw[513] != (byte)'F' ||
                Raw[514] != (byte)'I' || Raw[515] != (byte)' ')
                return false;

            var HeaderSize = ReadU32(Raw, 512 + 12);
            if ((HeaderSize < 92) || (HeaderSize > 512))
                return false;

            var EntriesLba = ReadU64(Raw, 512 + 72);
            var Count = ReadU32(Raw, 512 + 80);
            var EntrySize = ReadU32(Raw, 512 + 84);
            if ((Count == 0) || (Count > 4096) || (EntrySize < 128) || (EntrySize > 4096))
                return false;

            var EntriesOffset = (long)EntriesLba * SectorSize;
            if ((EntriesOffset < 0) || (EntriesOffset + (long)Count * EntrySize > Raw.Length))
                return false;

            var DiskSectors = Raw.Length / SectorSize;
            for (var I = 0; I < Count; I++)
            {
                var Base = (int)(EntriesOffset + (long)I * EntrySize);
                var TypeGuid = ReadGuid(Raw, Base);
                if (TypeGuid == Guid.Empty)
                    continue;

                var First = ReadU64(Raw, Base + 32);
                var Last = ReadU64(Raw, Base + 40);
                if ((Last < First) || (First == 0) || (Last >= (ulong)DiskSectors))
                    continue;

                var Name = ReadUtf16(Raw, Base + 56, 72).TrimEnd('\0', ' ');
                if (Name.Length == 0)
                    Name = "partition" + (I + 1);

                Image.Partitions.Add(new DiskPartition
                {
                    Index = I,
                    Name = Name,
                    IsGpt = true,
                    TypeGuid = TypeGuid.ToString(),
                    PartitionGuid = ReadGuid(Raw, Base + 16).ToString(),
                    StartLba = (long)First,
                    SectorCount = (long)(Last - First + 1)
                });
            }

            return Image.Partitions.Count > 0;
        }

        private static Guid ReadGuid(byte[] Raw, int Offset)
        {
            if (Offset + 16 > Raw.Length)
                return Guid.Empty;
            var Bytes = new byte[16];
            Buffer.BlockCopy(Raw, Offset, Bytes, 0, 16);
            // GPT stores the first three GUID fields little-endian.
            Array.Reverse(Bytes, 0, 4);
            Array.Reverse(Bytes, 4, 2);
            Array.Reverse(Bytes, 6, 2);
            return new Guid(Bytes);
        }

        private static string ReadUtf16(byte[] Raw, int Offset, int Size)
        {
            if (Offset + Size > Raw.Length)
                Size = Math.Max(0, Raw.Length - Offset);
            return Encoding.Unicode.GetString(Raw, Offset, Size);
        }

        private static uint ReadU32(byte[] Raw, int Offset) =>
            (uint)(Raw[Offset] | (Raw[Offset + 1] << 8) | (Raw[Offset + 2] << 16) | (Raw[Offset + 3] << 24));

        private static ulong ReadU64(byte[] Raw, int Offset) =>
            ReadU32(Raw, Offset) | ((ulong)ReadU32(Raw, Offset + 4) << 32);

        public static void WriteU32(byte[] Raw, int Offset, uint Value)
        {
            Raw[Offset] = (byte)Value;
            Raw[Offset + 1] = (byte)(Value >> 8);
            Raw[Offset + 2] = (byte)(Value >> 16);
            Raw[Offset + 3] = (byte)(Value >> 24);
        }
    }
}