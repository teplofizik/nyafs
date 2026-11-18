using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Builds an MBR/GPT disk image from a declarative layout. Payloads are placed at
    /// their offsets; the rest of the disk stays zero-filled (disk images, not flash).
    /// </summary>
    public static class DiskImageBuilder
    {
        private const int Sector = DiskImage.SectorSize;
        private const int GptEntryCount = 128;
        private const int GptEntrySize = 128;
        private const int GptEntriesSectors = (GptEntryCount * GptEntrySize) / Sector;
        public const string LinuxDataGuid = "0FC63DAF-8483-4772-8E79-3D69D8477DE4";

        private class Part
        {
            public string Name;
            public long Offset;
            public long Size;
            public byte[] Data;
            public string FilePath;
            public long DataLength;
            public byte MbrType;
            public Guid TypeGuid;
            public Guid UniqueGuid;
        }

        public static byte[] Build(ImageBuildNode Root, PluginRegistry Plugins, out string Error)
        {
            using (var Stream = new System.IO.MemoryStream())
            {
                if (!BuildTo(Stream, Root, Plugins, out Error))
                    return null;

                return Stream.ToArray();
            }
        }

        /// <summary>
        /// Stream a disk image to the output without allocating the whole disk.
        /// The stream must be writable; seeking is not required.
        /// </summary>
        public static bool BuildTo(System.IO.Stream Output, ImageBuildNode Root,
            PluginRegistry Plugins, out string Error)
        {
            Error = null;

            if ((Output == null) || !Output.CanWrite)
            {
                Error = "Disk output stream is not writable";
                return false;
            }

            var Table = Get(Root, "table", "gpt").ToLowerInvariant();
            if ((Table != "mbr") && (Table != "gpt"))
            {
                Error = "disk table must be 'mbr' or 'gpt'";
                return false;
            }

            Root.Options.TryGetValue("plan.base", out var BaseDir);
            Root.Options.TryGetValue("plan.blobs", out var BlobDir);

            var Align = Option(Root, "align", 1024 * 1024) ?? (1024 * 1024);
            var TotalOption = Option(Root, "total", null);
            var Seed = Get(Root, "seed", "nya");

            var Parts = new List<Part>();
            long Cursor = 1024 * 1024;

            foreach (var Child in Root.Children)
            {
                byte[] Data = null;
                string FilePath = null;
                long DataLength;
                bool Skip;

                if (PlanContentFile.TryGet(Child, BaseDir, out FilePath, out DataLength, out Skip, out var FileError))
                {
                    if (!String.IsNullOrEmpty(FileError))
                    {
                        Error = FileError;
                        return false;
                    }
                }
                else
                {
                    if (!PlanNodeSerializer.TrySerialize(Child, Plugins, BaseDir, BlobDir,
                        out Data, out Skip, out var Err))
                    {
                        Error = Err;
                        return false;
                    }

                    DataLength = Data?.Length ?? 0;
                }

                if (Skip)
                    continue;

                long Size;
                if (Child.SizeAuto || !Child.Size.HasValue)
                    Size = RoundUp(DataLength, Sector);
                else
                    Size = Child.Size.Value;

                if (Size < DataLength)
                {
                    Error = $"Partition '{Child.Name}' is too small for its content ({Size} < {DataLength} bytes)";
                    return false;
                }

                Size = RoundUp(Size, Sector);

                var Offset = Child.Offset ?? RoundUp(Cursor, Align);
                if ((Offset % Sector) != 0)
                {
                    Error = $"Partition '{Child.Name}' offset 0x{Offset:x} is not sector aligned";
                    return false;
                }

                if (Table == "mbr")
                {
                    if (Parts.Count >= 4)
                    {
                        Error = "MBR supports at most 4 partitions; use 'table: gpt'";
                        return false;
                    }

                    if ((Offset / Sector) > 0xFFFFFFFF || (Size / Sector) > 0xFFFFFFFF)
                    {
                        Error = $"Partition '{Child.Name}' does not fit into MBR addressing";
                        return false;
                    }
                }

                var TypeGuidText = Get(Child, "type-guid", LinuxDataGuid);
                if (!Guid.TryParse(TypeGuidText, out var TypeGuid))
                {
                    Error = $"Partition '{Child.Name}' has an invalid type-guid";
                    return false;
                }

                var UniqueGuidText = Get(Child, "partition-guid", null);
                var UniqueGuid = (!String.IsNullOrEmpty(UniqueGuidText) && Guid.TryParse(UniqueGuidText, out var Explicit))
                    ? Explicit
                    : GuidFromName(Seed, Child.Name);

                Parts.Add(new Part
                {
                    Name = Child.Name,
                    Offset = Offset,
                    Size = Size,
                    Data = Data,
                    FilePath = FilePath,
                    DataLength = DataLength,
                    MbrType = (byte)(Option(Child, "mbr-type", 0x83) & 0xFF),
                    TypeGuid = TypeGuid,
                    UniqueGuid = UniqueGuid
                });

                Cursor = Math.Max(Cursor, Offset + Size);
            }

            long Total = TotalOption ?? RoundUp(Cursor + Align, Align);
            Total = RoundUp(Total, Sector);

            if (Total < Cursor)
            {
                Error = $"Disk size 0x{Total:x} is smaller than the last partition end 0x{Cursor:x}";
                return false;
            }

            var Sorted = new List<Part>(Parts);
            Sorted.Sort((A, B) => A.Offset.CompareTo(B.Offset));

            long Pos = 0;

            if (Table == "mbr")
            {
                var Mbr = new byte[Sector];
                WriteMbr(Mbr, Parts);
                Output.Write(Mbr, 0, Mbr.Length);
                Pos = Mbr.Length;
            }
            else
            {
                long TotalSectors = Total / Sector;
                if (TotalSectors < 2 + GptEntriesSectors + GptEntriesSectors + 2)
                {
                    Error = "Disk is too small for a GPT";
                    return false;
                }

                var Entries = new byte[GptEntryCount * GptEntrySize];
                for (int I = 0; I < Parts.Count; I++)
                {
                    var Part = Parts[I];
                    var Base = I * GptEntrySize;

                    if (((Part.Offset / Sector) < 34) || ((Part.Offset + Part.Size) > Total))
                    {
                        Error = $"Partition '{Part.Name}' is outside the GPT usable area";
                        return false;
                    }

                    WriteGuid(Entries, Base, Part.TypeGuid);
                    WriteGuid(Entries, Base + 16, Part.UniqueGuid);
                    WriteU64(Entries, Base + 32, (ulong)(Part.Offset / Sector));
                    WriteU64(Entries, Base + 40, (ulong)((Part.Offset + Part.Size) / Sector - 1));
                    WriteName(Entries, Base + 56, Part.Name);
                }

                var EntriesCrc = Crc32(Entries);
                var DiskGuid = GuidFromName(Seed, "disk");
                long BackupEntriesLba = TotalSectors - 1 - GptEntriesSectors;
                long FirstUsable = 2 + GptEntriesSectors;
                long LastUsable = BackupEntriesLba - 1;

                var Protective = new byte[Sector];
                var Entry = 446;
                Protective[Entry + 4] = 0xEE;
                DiskImage.WriteU32(Protective, Entry + 8, 1);
                DiskImage.WriteU32(Protective, Entry + 12, (uint)Math.Min(TotalSectors - 1, 0xFFFFFFFFL));
                Protective[510] = 0x55;
                Protective[511] = 0xAA;
                Output.Write(Protective, 0, Protective.Length);
                Pos = Protective.Length;

                var PrimaryHeader = new byte[Sector];
                WriteGptHeader(PrimaryHeader, 0, 1, TotalSectors - 1, FirstUsable, LastUsable,
                    DiskGuid, 2, EntriesCrc);
                Output.Write(PrimaryHeader, 0, PrimaryHeader.Length);
                Pos += PrimaryHeader.Length;

                Output.Write(Entries, 0, Entries.Length);
                Pos += Entries.Length;

                WriteZeros(Output, FirstUsable * Sector - Pos);
                Pos = FirstUsable * Sector;

                foreach (var Part in Sorted)
                {
                    if (Part.Offset < Pos)
                    {
                        Error = $"Partition '{Part.Name}' overlaps the previous data";
                        return false;
                    }

                    WriteZeros(Output, Part.Offset - Pos);
                    if (!WritePart(Output, Part, out Error))
                        return false;

                    Pos = Part.Offset + Part.DataLength;

                    WriteZeros(Output, Part.Offset + Part.Size - Pos);
                    Pos = Part.Offset + Part.Size;
                }

                WriteZeros(Output, BackupEntriesLba * Sector - Pos);
                Pos = BackupEntriesLba * Sector;
                Output.Write(Entries, 0, Entries.Length);
                Pos += Entries.Length;

                var BackupHeader = new byte[Sector];
                WriteGptHeader(BackupHeader, 0, TotalSectors - 1, 1, FirstUsable, LastUsable,
                    DiskGuid, BackupEntriesLba, EntriesCrc);
                WriteZeros(Output, (TotalSectors - 1) * Sector - Pos);
                Output.Write(BackupHeader, 0, BackupHeader.Length);
                Pos = Total;
            }

            if (Table == "mbr")
            {
                foreach (var Part in Sorted)
                {
                    if (Part.Offset < Pos)
                    {
                        Error = $"Partition '{Part.Name}' overlaps the previous data";
                        return false;
                    }

                    WriteZeros(Output, Part.Offset - Pos);
                    if (!WritePart(Output, Part, out Error))
                        return false;

                    Pos = Part.Offset + Part.DataLength;

                    WriteZeros(Output, Part.Offset + Part.Size - Pos);
                    Pos = Part.Offset + Part.Size;
                }

                WriteZeros(Output, Total - Pos);
            }

            return true;
        }

        private static bool WritePart(System.IO.Stream Output, Part Part, out string Error)
        {
            Error = null;

            if (Part.FilePath != null)
            {
                try
                {
                    using (var Source = new System.IO.FileStream(Part.FilePath,
                        System.IO.FileMode.Open, System.IO.FileAccess.Read,
                        System.IO.FileShare.Read, 1024 * 1024, System.IO.FileOptions.SequentialScan))
                    {
                        if (Source.Length < Part.DataLength)
                        {
                            Error = $"Content file '{Part.FilePath}' is shorter than expected";
                            return false;
                        }

                        return CopyExactly(Source, Output, Part.DataLength, out Error);
                    }
                }
                catch (Exception E)
                {
                    Error = $"Cannot read content file '{Part.FilePath}': {E.Message}";
                    return false;
                }
            }

            if ((Part.Data == null) || (Part.Data.Length < Part.DataLength))
            {
                Error = $"Partition '{Part.Name}' has no content";
                return false;
            }

            Output.Write(Part.Data, 0, (int)Part.DataLength);
            return true;
        }

        private static bool CopyExactly(System.IO.Stream Source, System.IO.Stream Output,
            long Length, out string Error)
        {
            Error = null;
            var Buffer = new byte[1024 * 1024];

            while (Length > 0)
            {
                var Read = Source.Read(Buffer, 0, (int)Math.Min(Buffer.Length, Length));
                if (Read <= 0)
                {
                    Error = "Content file ended before the declared size";
                    return false;
                }

                Output.Write(Buffer, 0, Read);
                Length -= Read;
            }

            return true;
        }
        private static readonly byte[] ZeroBuffer = new byte[64 * 1024];

        private static void WriteZeros(System.IO.Stream Output, long Count)
        {
            if (Count <= 0)
                return;

            while (Count > 0)
            {
                var Chunk = (int)Math.Min(Count, ZeroBuffer.Length);
                Output.Write(ZeroBuffer, 0, Chunk);
                Count -= Chunk;
            }
        }
        private static void WriteMbr(byte[] Disk, List<Part> Parts)
        {
            for (int I = 0; I < Parts.Count; I++)
            {
                var Part = Parts[I];
                var Entry = 446 + I * 16;
                Disk[Entry + 0] = 0x00;
                Disk[Entry + 1] = 0x00;
                Disk[Entry + 2] = 0x00;
                Disk[Entry + 3] = 0x00;
                Disk[Entry + 4] = Part.MbrType;
                DiskImage.WriteU32(Disk, Entry + 8, (uint)(Part.Offset / Sector));
                DiskImage.WriteU32(Disk, Entry + 12, (uint)(Part.Size / Sector));
            }

            Disk[510] = 0x55;
            Disk[511] = 0xAA;
        }

        private static bool WriteGpt(byte[] Disk, List<Part> Parts, string Seed, out string Error)
        {
            Error = null;

            long TotalSectors = Disk.Length / Sector;
            if (TotalSectors < 2 + GptEntriesSectors + GptEntriesSectors + 2)
            {
                Error = "Disk is too small for a GPT";
                return false;
            }

            // Protective MBR
            var Entry = 446;
            Disk[Entry + 4] = 0xEE;
            DiskImage.WriteU32(Disk, Entry + 8, 1);
            DiskImage.WriteU32(Disk, Entry + 12, (uint)Math.Min(TotalSectors - 1, 0xFFFFFFFFL));
            Disk[510] = 0x55;
            Disk[511] = 0xAA;

            var Entries = new byte[GptEntryCount * GptEntrySize];
            for (int I = 0; I < Parts.Count; I++)
            {
                var Part = Parts[I];
                var Base = I * GptEntrySize;

                if ((Part.Offset / Sector) < 34 || (Part.Offset + Part.Size) > Disk.Length)
                {
                    Error = $"Partition '{Part.Name}' is outside the GPT usable area";
                    return false;
                }

                WriteGuid(Entries, Base, Part.TypeGuid);
                WriteGuid(Entries, Base + 16, Part.UniqueGuid);
                WriteU64(Entries, Base + 32, (ulong)(Part.Offset / Sector));
                WriteU64(Entries, Base + 40, (ulong)((Part.Offset + Part.Size) / Sector - 1));
                WriteName(Entries, Base + 56, Part.Name);
            }

            long PrimaryEntriesLba = 2;
            long BackupEntriesLba = TotalSectors - 1 - GptEntriesSectors;
            long FirstUsable = PrimaryEntriesLba + GptEntriesSectors;
            long LastUsable = BackupEntriesLba - 1;

            Buffer.BlockCopy(Entries, 0, Disk, (int)(PrimaryEntriesLba * Sector), Entries.Length);
            Buffer.BlockCopy(Entries, 0, Disk, (int)(BackupEntriesLba * Sector), Entries.Length);

            var EntriesCrc = Crc32(Entries);
            var DiskGuid = GuidFromName(Seed, "disk");

            WriteGptHeader(Disk, 1 * Sector, 1, TotalSectors - 1, FirstUsable, LastUsable,
                DiskGuid, PrimaryEntriesLba, EntriesCrc);
            WriteGptHeader(Disk, (TotalSectors - 1) * Sector, TotalSectors - 1, 1, FirstUsable, LastUsable,
                DiskGuid, BackupEntriesLba, EntriesCrc);

            return true;
        }

        private static void WriteGptHeader(byte[] Disk, long Offset, long Current, long Backup,
            long FirstUsable, long LastUsable, Guid DiskGuid, long EntriesLba, uint EntriesCrc)
        {
            var Base = (int)Offset;
            Array.Clear(Disk, Base, 512);

            Encoding.ASCII.GetBytes("EFI PART").CopyTo(Disk, Base);
            DiskImage.WriteU32(Disk, Base + 8, 0x00010000);
            DiskImage.WriteU32(Disk, Base + 12, 92);
            DiskImage.WriteU32(Disk, Base + 16, 0);
            DiskImage.WriteU32(Disk, Base + 20, 0);
            WriteU64(Disk, Base + 24, (ulong)Current);
            WriteU64(Disk, Base + 32, (ulong)Backup);
            WriteU64(Disk, Base + 40, (ulong)FirstUsable);
            WriteU64(Disk, Base + 48, (ulong)LastUsable);
            WriteGuid(Disk, Base + 56, DiskGuid);
            WriteU64(Disk, Base + 72, (ulong)EntriesLba);
            DiskImage.WriteU32(Disk, Base + 80, GptEntryCount);
            DiskImage.WriteU32(Disk, Base + 84, GptEntrySize);
            DiskImage.WriteU32(Disk, Base + 88, EntriesCrc);
            DiskImage.WriteU32(Disk, Base + 16, Crc32(Disk, Base, 92));
        }

        private static void WriteGuid(byte[] Data, int Offset, Guid Value)
        {
            var Bytes = Value.ToByteArray();
            Array.Reverse(Bytes, 0, 4);
            Array.Reverse(Bytes, 4, 2);
            Array.Reverse(Bytes, 6, 2);
            Buffer.BlockCopy(Bytes, 0, Data, Offset, 16);
        }

        private static void WriteName(byte[] Data, int Offset, string Name)
        {
            var Text = Name ?? "";
            var Bytes = Encoding.Unicode.GetBytes(Text);
            var Length = Math.Min(Bytes.Length, 72);
            Buffer.BlockCopy(Bytes, 0, Data, Offset, Length);
        }

        private static void WriteU64(byte[] Data, int Offset, ulong Value)
        {
            DiskImage.WriteU32(Data, Offset, (uint)Value);
            DiskImage.WriteU32(Data, Offset + 4, (uint)(Value >> 32));
        }

        private static Guid GuidFromName(string Seed, string Name)
        {
            using (var Sha = SHA256.Create())
            {
                var Hash = Sha.ComputeHash(Encoding.UTF8.GetBytes((Seed ?? "") + "\0" + (Name ?? "")));
                var Bytes = new byte[16];
                Array.Copy(Hash, Bytes, 16);
                Bytes[6] = (byte)((Bytes[6] & 0x0F) | 0x40);
                Bytes[8] = (byte)((Bytes[8] & 0x3F) | 0x80);
                return new Guid(Bytes);
            }
        }

        private static string Get(ImageBuildNode Node, string Key, string Default) =>
            Node.Options.TryGetValue(Key, out var Value) ? Value : Default;

        private static long? Option(ImageBuildNode Node, string Key, long? Default)
        {
            if (!Node.Options.TryGetValue(Key, out var Text) || String.IsNullOrWhiteSpace(Text))
                return Default;

            var Value = PlanDocument.ParseSize(Text, out var Auto);
            return (Auto || !Value.HasValue) ? Default : Value;
        }

        private static long RoundUp(long Value, long Align)
        {
            if (Align <= 1)
                return Value;
            return ((Value + Align - 1) / Align) * Align;
        }

        private static uint[] CrcTable = CreateTable();

        private static uint[] CreateTable()
        {
            var Table = new uint[256];
            for (uint I = 0; I < 256; I++)
            {
                var C = I;
                for (int K = 0; K < 8; K++)
                    C = ((C & 1) != 0) ? (0xEDB88320u ^ (C >> 1)) : (C >> 1);
                Table[I] = C;
            }
            return Table;
        }

        private static uint Crc32(byte[] Data) => Crc32(Data, 0, Data.Length);

        private static uint Crc32(byte[] Data, int Offset, int Length)
        {
            var Crc = 0xFFFFFFFFu;
            for (int I = 0; I < Length; I++)
                Crc = (Crc >> 8) ^ CrcTable[(Crc ^ Data[Offset + I]) & 0xFF];
            return Crc ^ 0xFFFFFFFF;
        }
    }
}
