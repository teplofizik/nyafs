using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;

namespace NyaFs.Filesystem.Fat
{
    /// <summary>
    /// FAT12/FAT16/FAT32 reader: parses the BPB and FAT, resolves paths and reads
    /// directory entries (8.3 + VFAT long names) and file contents.
    /// </summary>
    public class FatFsReader : IFilesystemReader
    {
        public readonly int BytesPerSec;
        public readonly int SecPerClus;
        public readonly int ClusterSize;
        public readonly int NumFats;
        public readonly int ReservedSectors;
        public readonly int FatSizeSectors;
        public readonly int RootEntryCount;
        public readonly int FatType;           // 12 / 16 / 32
        public readonly uint RootCluster;
        public readonly int RootDirSector;
        public readonly int RootDirSectors;
        public readonly int FirstDataSector;
        public readonly int ClusterCount;

        private readonly byte[] Raw;
        private readonly uint[] Fat;

        private class DirEntry
        {
            public string Name;
            public byte Attr;
            public uint Cluster;
            public uint Size;
            public bool IsDir => (Attr & 0x10) != 0;
            public bool IsVolume => (Attr & 0x08) != 0;
        }

        public FatFsReader(byte[] Data)
        {
            Raw = Data;

            if (!FatDetector.IsFat(Raw))
                throw new InvalidOperationException("Not a FAT image");

            BytesPerSec = Convert.ToInt32(Raw.ReadUInt16(11));
            SecPerClus = Raw[13];
            ReservedSectors = Convert.ToInt32(Raw.ReadUInt16(14));
            NumFats = Raw[16];
            RootEntryCount = Convert.ToInt32(Raw.ReadUInt16(17));
            uint totSec = Raw.ReadUInt16(19);
            if (totSec == 0)
                totSec = Raw.ReadUInt32(32);
            uint fatSz16 = Raw.ReadUInt16(22);
            uint fatSz32 = Raw.ReadUInt32(36);

            FatSizeSectors = Convert.ToInt32(fatSz16 != 0 ? fatSz16 : fatSz32);
            RootDirSectors = (RootEntryCount * 32 + BytesPerSec - 1) / BytesPerSec;
            RootDirSector = ReservedSectors + NumFats * FatSizeSectors;
            FirstDataSector = RootDirSector + RootDirSectors;
            ClusterSize = SecPerClus * BytesPerSec;
            ClusterCount = Convert.ToInt32((totSec - FirstDataSector) / SecPerClus);
            RootCluster = (fatSz16 != 0) ? 0u : Raw.ReadUInt32(44);
            FatType = FatDetector.DetectFatType(Convert.ToInt32(totSec), FirstDataSector, SecPerClus, fatSz16 != 0);

            Fat = LoadFat();
        }

        public FatFsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }

        public string FatTypeName => FatDetector.GetFatTypeName(FatType);

        // ---- FAT / clusters ----

        private uint Eoc => FatType == 12 ? 0xFFFu : (FatType == 16 ? 0xFFFFu : 0x0FFFFFFFu);
        private uint EocMin => FatType == 12 ? 0xFF8u : (FatType == 16 ? 0xFFF8u : 0x0FFFFFF8u);

        private uint[] LoadFat()
        {
            var res = new uint[ClusterCount + 2];
            int baseOff = ReservedSectors * BytesPerSec;

            if (FatType == 12)
            {
                for (int i = 0; i < res.Length; i++)
                {
                    int off = baseOff + i + (i / 2);
                    if (off + 1 >= Raw.Length) break;
                    int w = Raw[off] | (Raw[off + 1] << 8);
                    res[i] = (uint)(((i & 1) == 0) ? (w & 0x0FFF) : (w >> 4));
                }
            }
            else if (FatType == 16)
            {
                for (int i = 0; i < res.Length; i++)
                {
                    int off = baseOff + i * 2;
                    if (off + 1 >= Raw.Length) break;
                    res[i] = Raw.ReadUInt16(off);
                }
            }
            else
            {
                for (int i = 0; i < res.Length; i++)
                {
                    int off = baseOff + i * 4;
                    if (off + 3 >= Raw.Length) break;
                    res[i] = Raw.ReadUInt32(off) & 0x0FFFFFFF;
                }
            }

            return res;
        }

        private long ClusterOffset(uint Cluster) =>
            ((long)FirstDataSector + (Cluster - 2) * (long)SecPerClus) * BytesPerSec;

        private byte[] ReadCluster(uint Cluster)
        {
            var Res = new byte[ClusterSize];
            long Off = ClusterOffset(Cluster);
            if (Off >= Raw.Length)
                return Res;

            int Len = (int)Math.Min(ClusterSize, Raw.Length - Off);
            Array.Copy(Raw, Off, Res, 0, Len);
            return Res;
        }

        private List<uint> Chain(uint Start)
        {
            var Res = new List<uint>();
            uint c = Start;
            int Guard = ClusterCount + 8;

            while ((c >= 2) && (c < Fat.Length) && (Guard-- > 0))
            {
                Res.Add(c);
                uint next = Fat[c];
                if ((next >= EocMin) || (next < 2) || (next == c))
                    break;
                c = next;
            }

            return Res;
        }

        private byte[] ChainBytes(uint Start)
        {
            if (Start < 2)
                return new byte[0];

            var Parts = Chain(Start);
            var Res = new byte[Parts.Count * ClusterSize];
            for (int i = 0; i < Parts.Count; i++)
                Array.Copy(ReadCluster(Parts[i]), 0, Res, i * ClusterSize, ClusterSize);
            return Res;
        }

        private byte[] RootDirBytes()
        {
            long Off = (long)RootDirSector * BytesPerSec;
            int Len = RootDirSectors * BytesPerSec;
            var Res = new byte[Len];
            int Copy = (int)Math.Min(Len, Raw.Length - Off);
            if (Copy > 0)
                Array.Copy(Raw, Off, Res, 0, Copy);
            return Res;
        }

        // ---- directory parsing ----

        private static uint U16(byte[] D, int O) => BitConverter.ToUInt16(D, O);
        private static uint U32(byte[] D, int O) => BitConverter.ToUInt32(D, O);

        private static string DecodeShortName(byte[] D, int Off)
        {
            var Base = new StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                byte c = D[Off + i];
                if (c == 0x20) break;
                if ((i == 0) && (c == 0x05)) c = 0xE5;
                Base.Append((char)c);
            }

            var Ext = new StringBuilder();
            for (int i = 8; i < 11; i++)
            {
                byte c = D[Off + i];
                if (c == 0x20) break;
                Ext.Append((char)c);
            }

            return (Ext.Length > 0) ? Base + "." + Ext : Base.ToString();
        }

        private static string ExtractLfnPart(byte[] D, int Off)
        {
            var Res = new StringBuilder();
            void Take(int O, int Count)
            {
                for (int i = 0; i < Count; i++)
                {
                    int u = D[O + i * 2] | (D[O + i * 2 + 1] << 8);
                    if (u == 0x0000)
                        return;
                    if (u == 0xFFFF)
                        continue;
                    Res.Append((char)u);
                }
            }

            Take(Off + 1, 5);
            Take(Off + 14, 6);
            Take(Off + 28, 2);
            return Res.ToString();
        }

        private static byte LfnChecksum(byte[] D, int Off)
        {
            int Sum = 0;
            for (int i = 0; i < 11; i++)
                Sum = ((((Sum & 1) << 7) + (Sum >> 1) + D[Off + i]) & 0xFF);
            return (byte)Sum;
        }

        private List<DirEntry> ParseDirectory(byte[] Data)
        {
            var Res = new List<DirEntry>();
            Dictionary<int, string> Lfn = null;
            byte LfnSum = 0;

            for (int Off = 0; Off + 32 <= Data.Length; Off += 32)
            {
                byte First = Data[Off];
                if (First == 0x00)
                    break;
                if (First == 0xE5)
                {
                    Lfn = null;
                    continue;
                }

                byte Attr = Data[Off + 11];
                if (Attr == 0x0F)
                {
                    int Ord = First & 0x3F;
                    bool Last = (First & 0x40) != 0;
                    if (Last)
                    {
                        Lfn = new Dictionary<int, string>();
                        LfnSum = Data[Off + 13];
                    }
                    if (Lfn != null)
                        Lfn[Ord] = ExtractLfnPart(Data, Off);
                    continue;
                }

                uint Cluster = (uint)(U16(Data, Off + 26) | ((FatType == 32) ? (U16(Data, Off + 20) << 16) : 0));
                uint Size = U32(Data, Off + 28);

                string Name = null;
                if ((Lfn != null) && (Lfn.Count > 0) && (LfnChecksum(Data, Off) == LfnSum))
                {
                    var Sb = new StringBuilder();
                    for (int i = 1; i <= Lfn.Count; i++)
                        if (Lfn.ContainsKey(i))
                            Sb.Append(Lfn[i]);
                    Name = Sb.ToString();
                }
                Name = Name ?? DecodeShortName(Data, Off);
                Lfn = null;

                Res.Add(new DirEntry { Name = Name, Attr = Attr, Cluster = Cluster, Size = Size });
            }

            return Res;
        }

        private List<DirEntry> ReadDirectory(uint Cluster, bool IsRoot)
        {
            byte[] Data = (IsRoot && (FatType != 32)) ? RootDirBytes() : ChainBytes(Cluster);
            return ParseDirectory(Data);
        }

        private static bool NameEquals(string A, string B) => string.Equals(A, B, StringComparison.OrdinalIgnoreCase);

        private DirEntry Find(UInt32 Cluster, bool IsRoot, string Name)
        {
            foreach (var E in ReadDirectory(Cluster, IsRoot))
                if (!E.IsVolume && (NameEquals(E.Name, Name)))
                    return E;
            return null;
        }

        private bool TryResolve(string Path, out DirEntry Entry, out uint Cluster, out bool IsRoot)
        {
            Entry = null;
            Cluster = (FatType == 32) ? RootCluster : 0;
            IsRoot = true;

            if (string.IsNullOrEmpty(Path) || (Path == "/") || (Path == "."))
                return true;

            foreach (var Part in Path.Trim('/').Split('/'))
            {
                if (Part.Length == 0)
                    continue;

                var E = Find(Cluster, IsRoot, Part);
                if (E == null)
                    return false;

                Entry = E;
                Cluster = E.Cluster;
                IsRoot = false;
            }

            return true;
        }

        // ---- IFilesystemReader ----

        public FilesystemEntry[] ReadDir(string Path)
        {
            if (!TryResolve(Path, out var Self, out uint Cluster, out bool IsRoot))
                return null;

            if ((Self != null) && !Self.IsDir)
                return null;

            var Res = new List<FilesystemEntry>();
            foreach (var E in ReadDirectory(Cluster, IsRoot))
            {
                if (E.IsVolume)
                    continue;
                if ((E.Name == ".") || (E.Name == ".."))
                    continue;

                uint Mode = E.IsDir ? 0x1EDu : 0x1A4u;   // 0755 / 0644
                Res.Add(new FilesystemEntry(E.IsDir ? FilesystemItemType.Directory : FilesystemItemType.File,
                    Universal.Helper.FsHelper.CombinePath(Path, E.Name), 0, 0, Mode, E.Size));
            }

            return Res.ToArray();
        }

        public byte[] Read(string Path)
        {
            if (!TryResolve(Path, out var E, out _, out _))
                return null;
            if ((E == null) || E.IsDir)
                return null;

            var Data = ChainBytes(E.Cluster);
            if (E.Size == 0)
                return new byte[0];

            var Res = new byte[Math.Min(E.Size, (uint)Data.Length)];
            Array.Copy(Data, 0, Res, 0, Res.Length);
            return Res;
        }

        public string ReadLink(string Path) => null;

        public DeviceInfo ReadDevice(string Path) => null;
    }
}
