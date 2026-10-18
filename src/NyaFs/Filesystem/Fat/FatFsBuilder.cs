using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.Filesystem.Universal;

namespace NyaFs.Filesystem.Fat
{
    /// <summary>
    /// FAT12/FAT16/FAT32 image builder (BPB, FAT table, root directory, data clusters,
    /// VFAT long names). The directory tree is filled via IFilesystemBuilder; the image
    /// is produced by GetFilesystemImage().
    /// </summary>
    public class FatFsBuilder : IFilesystemBuilder
    {
        private class Node
        {
            public string Name;
            public bool IsDir;
            public byte[] Content;
            public readonly List<Node> Children = new List<Node>();
        }

        private Node Root;

        public readonly long TotalBytes;
        public readonly int RequestedFatType;      // 0 = auto (12/16/32 by size)
        public readonly int BytesPerSec;

        public ushort DosTime;
        public ushort DosDate;

        private int FatType;
        private int SecPerClus;
        private int FatSizeSectors;
        private int ReservedSectors;
        private readonly int NumFats = 2;
        private int RootEntryCount;
        private int RootDirSectors;
        private int FirstDataSector;
        private int ClusterCount;
        private uint RootCluster;
        private int ClusterSize;

        private byte[] Image;
        private uint[] FatEntries;
        private UInt32 NextCluster;

        public FatFsBuilder(long TotalBytes, int FatType = 0, int BytesPerSec = 512)
        {
            this.TotalBytes = TotalBytes;
            this.RequestedFatType = FatType;
            this.BytesPerSec = BytesPerSec;

            var Now = DateTime.Now;
            DosTime = (ushort)((Now.Hour << 11) | (Now.Minute << 5) | (Now.Second / 2));
            DosDate = (ushort)(((Now.Year - 1980) << 9) | (Now.Month << 5) | Now.Day);
        }

        // ---- IFilesystemBuilder ----

        public void Directory(string Path, uint User, uint Group, uint Mode)
        {
            if (IsRoot(Path))
            {
                if (Root == null)
                    Root = new Node { Name = "", IsDir = true };
                else
                    throw new ArgumentException("Root directory already exists");
                return;
            }

            var Parent = FindParent(Path);
            if (Parent == null)
                throw new InvalidOperationException($"No parent directory for '{Path}'");

            Parent.Children.Add(new Node { Name = Name(Path), IsDir = true });
        }

        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode)
        {
            var Parent = FindParent(Path);
            if (Parent == null)
                throw new InvalidOperationException($"No parent directory for '{Path}'");

            Parent.Children.Add(new Node { Name = Name(Path), IsDir = false, Content = Content ?? new byte[0] });
        }

        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) => throw new NotSupportedException("FAT does not support symlinks");
        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) => throw new NotSupportedException("FAT does not support device nodes");
        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) => throw new NotSupportedException("FAT does not support device nodes");
        public void Fifo(string Path, uint User, uint Group, uint Mode) => throw new NotSupportedException("FAT does not support fifo");
        public void Socket(string Path, uint User, uint Group, uint Mode) => throw new NotSupportedException("FAT does not support sockets");

        // ---- path helpers ----

        private static bool IsRoot(string Path) => (Path == "/") || (Path == ".") || (Path == "") || (Path == null);
        private static string Name(string Path) => Universal.Helper.FsHelper.GetName(Path);

        private Node Find(string Path)
        {
            if (Root == null)
                return null;
            if (IsRoot(Path))
                return Root;

            var Cur = Root;
            foreach (var Part in Path.Trim('/').Split('/'))
            {
                if (Part.Length == 0)
                    continue;

                Node Next = null;
                foreach (var C in Cur.Children)
                    if (C.IsDir && (string.Equals(C.Name, Part, StringComparison.OrdinalIgnoreCase))) { Next = C; break; }

                if (Next == null)
                    return null;
                Cur = Next;
            }
            return Cur;
        }

        private Node FindParent(string Path) => Find(Universal.Helper.FsHelper.GetParentDirPath(Path));

        // ---- image build ----

        public byte[] GetFilesystemImage()
        {
            if (Root == null)
                throw new InvalidOperationException("No root directory");

            if ((TotalBytes % BytesPerSec) != 0)
                throw new ArgumentException("Total size must be a multiple of the sector size");

            int TotalSectors = (int)(TotalBytes / BytesPerSec);
            SelectGeometry(TotalSectors);

            ClusterSize = SecPerClus * BytesPerSec;
            RootDirSectors = (RootEntryCount * 32 + BytesPerSec - 1) / BytesPerSec;
            FirstDataSector = ReservedSectors + NumFats * FatSizeSectors + RootDirSectors;

            FatEntries = new uint[ClusterCount + 2];
            FatEntries[0] = (FatType == 32) ? 0x0FFFFFF8u : (FatType == 16 ? 0xFFF8u : 0xFF8u);
            FatEntries[1] = (FatType == 32) ? 0x0FFFFFFFu : (FatType == 16 ? 0xFFFFu : 0xFFFu);

            NextCluster = 2;
            RootCluster = (FatType == 32) ? Alloc(1)[0] : 0u;

            Image = new byte[(long)TotalSectors * BytesPerSec];

            var RootEntries = new List<byte[]>();
            foreach (var C in Root.Children)
                RootEntries.Add(BuildEntry(C, RootCluster));

            if (FatType == 32)
            {
                var Data = LayoutDirectory(RootEntries, RootCluster, 0, false);
                ExtendChain(RootCluster, Math.Max(1, (Data.Length + ClusterSize - 1) / ClusterSize));
                WriteChain(GetChain(RootCluster), Data);
            }
            else
            {
                var Data = new byte[RootDirSectors * BytesPerSec];
                int Off = 0;
                foreach (var E in RootEntries)
                {
                    if (Off + E.Length > Data.Length)
                        throw new InvalidOperationException("FAT root directory overflow (too many entries)");
                    Array.Copy(E, 0, Data, Off, E.Length);
                    Off += E.Length;
                }
                long Base = (long)(ReservedSectors + NumFats * FatSizeSectors) * BytesPerSec;
                Array.Copy(Data, 0, Image, Base, Data.Length);
            }

            WriteBoot();

            var Fat = PackFat();
            for (int i = 0; i < NumFats; i++)
            {
                long Off = (long)(ReservedSectors + i * FatSizeSectors) * BytesPerSec;
                Array.Copy(Fat, 0, Image, Off, Fat.Length);
            }

            return Image;
        }

        // ---- geometry ----

        private void SelectGeometry(int TotalSectors)
        {
            int[] SpcList = { 1, 2, 4, 8, 16, 32, 64, 128 };
            var Types = (RequestedFatType != 0) ? new[] { RequestedFatType } : new[] { 12, 16, 32 };

            foreach (var Type in Types)
            {
                foreach (var Spc in SpcList)
                {
                    if ((Type == 12) && (Spc > 1))
                        continue;

                    int Reserved = (Type == 32) ? 32 : 1;
                    int RootEnt = (Type == 32) ? 0 : (Type == 12 ? 224 : 512);
                    int RootSec = (RootEnt * 32 + BytesPerSec - 1) / BytesPerSec;

                    if (Compute(Type, TotalSectors, Spc, Reserved, RootSec, out int Fatsz, out int Clusters))
                    {
                        FatType = Type;
                        SecPerClus = Spc;
                        ReservedSectors = Reserved;
                        RootEntryCount = RootEnt;
                        FatSizeSectors = Fatsz;
                        ClusterCount = Clusters;
                        return;
                    }
                }
            }

            throw new InvalidOperationException($"Cannot fit a FAT filesystem into {TotalSectors} sectors");
        }

        private bool Compute(int Type, int TotalSectors, int Spc, int Reserved, int RootSec, out int Fatsz, out int Clusters)
        {
            int Lo = (Type == 12) ? 1 : (Type == 16 ? 4085 : 65525);
            int Hi = (Type == 12) ? 4084 : (Type == 16 ? 65524 : 0x0FFFFFF4);

            Fatsz = 1;
            for (int i = 0; i < 1000; i++)
            {
                int DataSectors = TotalSectors - Reserved - NumFats * Fatsz - RootSec;
                if (DataSectors <= 0)
                {
                    Clusters = 0;
                    return false;
                }

                int ClustersNow = DataSectors / Spc;
                int Need = (Type == 12)
                    ? ((ClustersNow + 2) * 3 + BytesPerSec - 1) / BytesPerSec
                    : ((ClustersNow + 2) * (Type / 8) + BytesPerSec - 1) / BytesPerSec;

                if ((Need <= Fatsz) && (ClustersNow >= Lo) && (ClustersNow <= Hi))
                {
                    Clusters = ClustersNow;
                    return true;
                }

                Fatsz = Math.Max(Fatsz + 1, Need);
            }

            Clusters = 0;
            return false;
        }

        // ---- allocation ----

        private uint EocValue => (FatType == 32) ? 0x0FFFFFFFu : (FatType == 16 ? 0xFFFFu : 0xFFFu);
        private uint EocMin => (FatType == 32) ? 0x0FFFFFF8u : (FatType == 16 ? 0xFFF8u : 0xFF8u);

        private List<uint> Alloc(int Count)
        {
            var Res = new List<uint>();
            if (Count <= 0)
                return Res;

            if ((NextCluster - 2) + Count > ClusterCount)
                throw new InvalidOperationException("FAT image is full");

            for (int i = 0; i < Count; i++)
                Res.Add(NextCluster++);
            for (int i = 0; i < Res.Count - 1; i++)
                FatEntries[Res[i]] = Res[i + 1];
            FatEntries[Res[Res.Count - 1]] = EocValue;
            return Res;
        }

        private void ExtendChain(uint Start, int TotalClusters)
        {
            uint Cur = Start;
            int Have = 1;
            while (Have < TotalClusters)
            {
                if ((NextCluster - 2) >= ClusterCount)
                    throw new InvalidOperationException("FAT image is full");
                uint N = NextCluster++;
                FatEntries[Cur] = N;
                Cur = N;
                Have++;
            }
            FatEntries[Cur] = EocValue;
        }

        private List<uint> GetChain(uint Start)
        {
            var Res = new List<uint>();
            uint C = Start;
            int Guard = ClusterCount + 8;
            while ((C >= 2) && (C < FatEntries.Length) && (Guard-- > 0))
            {
                Res.Add(C);
                uint N = FatEntries[C];
                if ((N >= EocMin) || (N < 2) || (N == C))
                    break;
                C = N;
            }
            return Res;
        }

        private long ClusterOffset(uint Cluster) =>
            ((long)FirstDataSector + (Cluster - 2) * (long)SecPerClus) * BytesPerSec;

        private void WriteChain(List<uint> Chain, byte[] Data)
        {
            for (int i = 0; i < Chain.Count; i++)
            {
                int Len = Math.Min(ClusterSize, Data.Length - i * ClusterSize);
                if (Len > 0)
                    Array.Copy(Data, i * ClusterSize, Image, ClusterOffset(Chain[i]), Len);
            }
        }

        private byte[] PackFat()
        {
            var Res = new byte[FatSizeSectors * BytesPerSec];
            if (FatType == 12)
            {
                for (int i = 0; i < FatEntries.Length; i++)
                {
                    int Off = i + (i / 2);
                    if (Off + 1 >= Res.Length) break;
                    if ((i & 1) == 0)
                    {
                        Res[Off] = (byte)FatEntries[i];
                        Res[Off + 1] = (byte)((Res[Off + 1] & 0xF0) | ((FatEntries[i] >> 8) & 0x0F));
                    }
                    else
                    {
                        Res[Off] = (byte)((Res[Off] & 0x0F) | ((FatEntries[i] << 4) & 0xF0));
                        Res[Off + 1] = (byte)((FatEntries[i] >> 4) & 0xFF);
                    }
                }
            }
            else if (FatType == 16)
            {
                for (int i = 0; i < FatEntries.Length; i++)
                {
                    int Off = i * 2;
                    if (Off + 1 >= Res.Length) break;
                    Res[Off] = (byte)FatEntries[i];
                    Res[Off + 1] = (byte)(FatEntries[i] >> 8);
                }
            }
            else
            {
                for (int i = 0; i < FatEntries.Length; i++)
                {
                    int Off = i * 4;
                    if (Off + 3 >= Res.Length) break;
                    Res[Off] = (byte)FatEntries[i];
                    Res[Off + 1] = (byte)(FatEntries[i] >> 8);
                    Res[Off + 2] = (byte)(FatEntries[i] >> 16);
                    Res[Off + 3] = (byte)(FatEntries[i] >> 24);
                }
            }
            return Res;
        }

        // ---- entries ----

        private byte[] BuildEntry(Node N, uint ParentCluster)
        {
            string Short = ShortNameFor(N.Name, out string LfnName);

            uint Cluster = 0;
            uint Size = 0;

            if (N.IsDir)
            {
                Cluster = Alloc(1)[0];
                var Sub = new List<byte[]>();
                foreach (var C in N.Children)
                    Sub.Add(BuildEntry(C, Cluster));

                var Data = LayoutDirectory(Sub, Cluster, ParentCluster);
                ExtendChain(Cluster, Math.Max(1, (Data.Length + ClusterSize - 1) / ClusterSize));
                WriteChain(GetChain(Cluster), Data);
            }
            else
            {
                Size = (uint)N.Content.Length;
                if (N.Content.Length > 0)
                {
                    int Need = (N.Content.Length + ClusterSize - 1) / ClusterSize;
                    var Chain = Alloc(Need);
                    WriteChain(Chain, N.Content);
                    Cluster = Chain[0];
                }
            }

            return PackEntry(Short, LfnName, N.IsDir, Cluster, Size);
        }

        private byte[] LayoutDirectory(List<byte[]> Entries, uint Self, uint Parent, bool WithDots = true)
        {
            var Body = new List<byte>();
            if (WithDots)
            {
                Body.AddRange(DotEntry(".", 0x10, Self));
                Body.AddRange(DotEntry("..", 0x10, Parent));
            }
            foreach (var E in Entries)
                Body.AddRange(E);

            int Len = Math.Max(ClusterSize, ((Body.Count + ClusterSize - 1) / ClusterSize) * ClusterSize);
            var Res = new byte[Len];
            Body.CopyTo(Res, 0);
            return Res;
        }

        // ---- names ----

        private static string Sanitize(string S)
        {
            var Sb = new StringBuilder();
            const string Allowed = "_$%'-@~`!#&(){}^";
            foreach (var Ch in S.ToUpperInvariant())
            {
                if (((Ch >= 'A') && (Ch <= 'Z')) || ((Ch >= '0') && (Ch <= '9')) || (Allowed.IndexOf(Ch) >= 0))
                    Sb.Append(Ch);
                else
                    Sb.Append('_');
            }
            return Sb.ToString();
        }

        private string ShortNameFor(string Name, out string LfnName)
        {
            LfnName = null;

            string Base = Name;
            string Ext = "";
            int Dot = Name.LastIndexOf('.');
            if (Dot > 0)
            {
                Base = Name.Substring(0, Dot);
                Ext = Name.Substring(Dot + 1);
            }

            string SBase = Sanitize(Base);
            string SExt = Sanitize(Ext);

            bool Fits83 = (SBase.Length <= 8) && (SBase.Length > 0) && (SExt.Length <= 3) && (Name == Name.ToUpperInvariant());

            if (Fits83)
                return SBase.PadRight(8) + SExt.PadRight(3);

            string Trunc = (SBase.Length > 6) ? SBase.Substring(0, 6) : SBase;
            if (Trunc.Length == 0)
                Trunc = "FILE";
            LfnName = Name;
            return (Trunc + "~1").PadRight(8).Substring(0, 8) + SExt.PadRight(3).Substring(0, 3);
        }

        private byte[] PackEntry(string Short11, string LfnName, bool IsDir, uint Cluster, uint Size)
        {
            var Res = new List<byte>();
            if (!string.IsNullOrEmpty(LfnName))
                foreach (var L in LfnEntries(LfnName, Short11))
                    Res.AddRange(L);

            var E = new byte[32];
            Encoding.ASCII.GetBytes(Short11).CopyTo(E, 0);
            E[11] = IsDir ? (byte)0x10 : (byte)0x20;
            PutU16(E, 14, DosTime);
            PutU16(E, 16, DosDate);
            PutU16(E, 18, DosDate);
            PutU16(E, 20, (ushort)(Cluster >> 16));
            PutU16(E, 22, DosTime);
            PutU16(E, 24, DosDate);
            PutU16(E, 26, (ushort)(Cluster & 0xFFFF));
            PutU32(E, 28, Size);
            Res.AddRange(E);
            return Res.ToArray();
        }

        private static byte[] DotEntry(string Name, byte Attr, uint Cluster)
        {
            var E = new byte[32];
            for (int i = 0; i < 11; i++) E[i] = 0x20;
            var B = Encoding.ASCII.GetBytes(Name);
            Array.Copy(B, 0, E, 0, Math.Min(11, B.Length));
            E[11] = Attr;
            PutU16(E, 26, (ushort)(Cluster & 0xFFFF));
            PutU16(E, 20, (ushort)(Cluster >> 16));
            return E;
        }

        private static byte LfnChecksum(byte[] Short11)
        {
            int Sum = 0;
            for (int i = 0; i < 11; i++)
                Sum = (((Sum & 1) << 7) + (Sum >> 1) + Short11[i]) & 0xFF;
            return (byte)Sum;
        }

        private static List<byte[]> LfnEntries(string Name, string Short11)
        {
            var Units = new List<int>();
            foreach (var Ch in Name)
                Units.Add(Ch);
            Units.Add(0x0000);
            while ((Units.Count % 13) != 0)
                Units.Add(0xFFFF);

            int Chunks = Units.Count / 13;
            byte Sum = LfnChecksum(Encoding.ASCII.GetBytes(Short11));
            var Res = new List<byte[]>();

            for (int i = Chunks - 1; i >= 0; i--)
            {
                var E = new byte[32];
                E[0] = (byte)((i + 1) | ((i == Chunks - 1) ? 0x40 : 0));
                for (int j = 0; j < 13; j++)
                {
                    int U = Units[i * 13 + j];
                    int Off = (j < 5) ? (1 + j * 2) : (j < 11 ? (14 + (j - 5) * 2) : (28 + (j - 11) * 2));
                    E[Off] = (byte)U;
                    E[Off + 1] = (byte)(U >> 8);
                }
                E[11] = 0x0F;
                E[13] = Sum;
                Res.Add(E);
            }

            return Res;
        }

        // ---- boot sector ----

        private void WriteBoot()
        {
            var B = new byte[BytesPerSec];
            B[0] = 0xEB;
            B[1] = (byte)(FatType == 32 ? 0x58 : 0x3C);
            B[2] = 0x90;
            Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(B, 3);
            PutU16(B, 11, (ushort)BytesPerSec);
            B[13] = (byte)SecPerClus;
            PutU16(B, 14, (ushort)ReservedSectors);
            B[16] = (byte)NumFats;
            PutU16(B, 17, (ushort)RootEntryCount);

            int TotalSectors = (int)(TotalBytes / BytesPerSec);
            if (TotalSectors < 0x10000)
            {
                PutU16(B, 19, (ushort)TotalSectors);
                PutU32(B, 32, 0);
            }
            else
            {
                PutU16(B, 19, 0);
                PutU32(B, 32, (uint)TotalSectors);
            }

            B[21] = 0xF8;
            PutU16(B, 22, (ushort)(FatType == 32 ? 0 : FatSizeSectors));
            PutU16(B, 24, 32);
            PutU16(B, 26, 64);
            PutU32(B, 28, 0);

            if (FatType == 32)
            {
                PutU32(B, 36, (uint)FatSizeSectors);
                PutU16(B, 40, 0);
                PutU16(B, 42, 0);
                PutU32(B, 44, RootCluster);
                PutU16(B, 48, 1);
                PutU16(B, 50, 6);
                B[64] = 0x80;
                B[66] = 0x29;
                PutU32(B, 67, 0x12345678u);
                Encoding.ASCII.GetBytes("NO NAME    ").CopyTo(B, 71);
                Encoding.ASCII.GetBytes("FAT32   ").CopyTo(B, 82);
            }
            else
            {
                B[36] = 0x80;
                B[38] = 0x29;
                PutU32(B, 39, 0x12345678u);
                Encoding.ASCII.GetBytes("NO NAME    ").CopyTo(B, 43);
                Encoding.ASCII.GetBytes(FatType == 12 ? "FAT12   " : "FAT16   ").CopyTo(B, 54);
            }

            B[510] = 0x55;
            B[511] = 0xAA;
            Array.Copy(B, 0, Image, 0, BytesPerSec);

            if (FatType == 32)
            {
                Array.Copy(B, 0, Image, 6L * BytesPerSec, BytesPerSec);

                var Fsi = new byte[BytesPerSec];
                PutU32(Fsi, 0, 0x41615252u);
                PutU32(Fsi, 484, 0x61417272u);
                PutU32(Fsi, 488, (uint)Math.Max(0, ClusterCount - ((int)NextCluster - 2)));
                PutU32(Fsi, 492, NextCluster);
                PutU32(Fsi, 508, 0xAA550000u);
                Array.Copy(Fsi, 0, Image, 1L * BytesPerSec, BytesPerSec);
                Array.Copy(Fsi, 0, Image, 7L * BytesPerSec, BytesPerSec);
            }
        }

        private static void PutU16(byte[] D, int O, ushort V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
        }

        private static void PutU32(byte[] D, int O, uint V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
            D[O + 2] = (byte)(V >> 16);
            D[O + 3] = (byte)(V >> 24);
        }
    }
}
