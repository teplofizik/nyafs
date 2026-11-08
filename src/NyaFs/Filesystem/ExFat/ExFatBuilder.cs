using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.ExFat
{
    public sealed class ExFatBuilder : IFilesystemBuilder
    {
        private sealed class Node
        {
            public string Path;
            public string Name;
            public Node Parent;
            public FilesystemItemType Type;
            public uint User;
            public uint Group;
            public uint Mode;
            public byte[] Data;
            public string LinkTarget;
            public readonly List<Node> Children = new List<Node>();

            public uint FirstCluster;
            public int ClusterCount;
            public long DataLength;
            public int EntryCount;
            public int DirectoryClusters;

            public bool IsDirectory => Type == FilesystemItemType.Directory;
        }

        private const int DefaultSize = 64 * 1024 * 1024;
        private const int SectorsPerCluster = 8;
        private const int FatOffset = 24;
        private const int BytesPerSector = 512;
        private const int ClusterSize = BytesPerSector * SectorsPerCluster;

        private readonly long TotalBytes;
        private readonly List<Node> Directories = new List<Node>();
        private readonly List<Node> Files = new List<Node>();
        private readonly ushort[] Upcase;
        private readonly byte[] UpcaseBytes;
        private readonly uint UpcaseChecksum;

        private Node Root;
        private int FatLength;
        private int ClusterHeapOffset;
        private int ClusterCount;

        private uint BitmapCluster;
        private int BitmapClusters;
        private uint UpcaseCluster;
        private int UpcaseClusters;
        private uint RootCluster;
        private int RootClusters;

        public ExFatBuilder(long TotalBytes = DefaultSize)
        {
            this.TotalBytes = Math.Max(TotalBytes, 2 * 1024 * 1024);
            UpcaseBytes = ExFatUpcaseData.Bytes();
            Upcase = ExFatShared.DecompressUpcase(UpcaseBytes);
            UpcaseChecksum = ExFatShared.UpcaseChecksum(UpcaseBytes);
        }

        public void Directory(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.Directory, null, null);

        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.File, Content ?? Array.Empty<byte>(), null);

        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.File, Encoding.UTF8.GetBytes(Target ?? ""), Target);

        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.File, Array.Empty<byte>(), null);

        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.File, Array.Empty<byte>(), null);

        public void Fifo(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.File, Array.Empty<byte>(), null);

        public void Socket(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.File, Array.Empty<byte>(), null);

        public byte[] GetFilesystemImage()
        {
            EnsureRoot();
            ComputeGeometry();
            CountEntries(Root);

            CollectDirectoryOrder(Root);
            AllocateClusters();

            var Image = new byte[(long)((TotalBytes / BytesPerSector) * BytesPerSector)];
            WriteFat(Image);
            WriteUpcase(Image);
            WriteBitmap(Image);
            WriteDirectories(Image);
            WriteFileData(Image);
            WriteBoot(Image);

            return Image;
        }

        private void EnsureRoot()
        {
            if (Root != null)
                return;

            Root = new Node
            {
                Path = "",
                Name = "",
                Type = FilesystemItemType.Directory,
                Mode = 0x1ED,
                EntryCount = 3
            };
        }

        private void GetOrCreate(string Path, uint User, uint Group, uint Mode, FilesystemItemType Type,
            byte[] Data, string LinkTarget)
        {
            EnsureRoot();
            var Normalized = ExFatSharedPath.Normalize(Path);
            if (Normalized.Length == 0)
            {
                Root.User = User;
                Root.Group = Group;
                Root.Mode = Mode & 0xFFF;
                return;
            }

            var Parts = Normalized.Split('/');
            var Current = Root;
            for (int i = 0; i < Parts.Length - 1; i++)
            {
                var Child = Current.Children.Find(C => C.Name == Parts[i] && C.IsDirectory);
                if (Child == null)
                {
                    Child = new Node
                    {
                        Name = Parts[i],
                        Path = ExFatSharedPath.Combine(Current.Path, Parts[i]),
                        Parent = Current,
                        Type = FilesystemItemType.Directory,
                        Mode = 0x1ED
                    };
                    Current.Children.Add(Child);
                }

                Current = Child;
            }

            var Name = Parts[Parts.Length - 1];
            var Existing = Current.Children.Find(C => C.Name == Name);
            if (Existing == null)
            {
                Existing = new Node
                {
                    Name = Name,
                    Path = ExFatSharedPath.Combine(Current.Path, Name),
                    Parent = Current
                };
                Current.Children.Add(Existing);
            }

            Existing.Type = Type;
            Existing.User = User;
            Existing.Group = Group;
            Existing.Mode = Mode & 0xFFF;
            Existing.Data = Data;
            Existing.LinkTarget = LinkTarget;
            Existing.DataLength = Data?.Length ?? 0;
        }

        private void ComputeGeometry()
        {
            long TotalSectors = Math.Max(1, TotalBytes / BytesPerSector);

            FatLength = 1;
            ClusterHeapOffset = FatOffset;
            ClusterCount = 0;

            for (int i = 0; i < 4; i++)
            {
                FatLength = (int)(((long)(ClusterCount + 2) * 4 + BytesPerSector - 1) / BytesPerSector);
                if (FatLength < 1)
                    FatLength = 1;

                ClusterHeapOffset = ExFatShared.AlignUp(FatOffset + FatLength, SectorsPerCluster);
                ClusterCount = (int)Math.Max(0, (TotalSectors - ClusterHeapOffset) / SectorsPerCluster);
            }

            if (ClusterCount < 32)
                throw new InvalidOperationException("ExFAT: image is too small");
        }

        private int CountEntries(Node Directory)
        {
            int Count = Directory.Parent == null ? 3 : 0;
            foreach (var Child in Directory.Children)
            {
                int NameEntries = (Child.Name.Length + 14) / 15;
                Count += 2 + NameEntries;
                if (Child.IsDirectory)
                    Count += CountEntries(Child);
            }

            Directory.EntryCount = Count;
            return Count;
        }

        private void CollectDirectoryOrder(Node Directory)
        {
            if (Directory.IsDirectory)
            {
                Directory.DirectoryClusters = Math.Max(1,
                    ExFatShared.AlignUp(Directory.EntryCount * ExFatShared.EntrySize, ClusterSize) / ClusterSize);
                Directories.Add(Directory);
            }

            foreach (var Child in Directory.Children)
            {
                if (Child.IsDirectory)
                    CollectDirectoryOrder(Child);
                else
                {
                    Child.ClusterCount = (int)((Child.DataLength + ClusterSize - 1) / ClusterSize);
                    if (Child.ClusterCount > 0)
                        Files.Add(Child);
                }
            }
        }

        private void AllocateClusters()
        {
            int Next = 2;
            BitmapClusters = Math.Max(1, ExFatShared.AlignUp((ClusterCount + 7) / 8, ClusterSize) / ClusterSize);
            UpcaseClusters = Math.Max(1, ExFatShared.AlignUp(UpcaseBytes.Length, ClusterSize) / ClusterSize);
            BitmapCluster = (uint)Next;
            Next += BitmapClusters;
            UpcaseCluster = (uint)Next;
            Next += UpcaseClusters;

            foreach (var Dir in Directories)
            {
                Dir.FirstCluster = (uint)Next;
                Dir.ClusterCount = Dir.DirectoryClusters;
                Next += Dir.ClusterCount;
            }

            foreach (var File in Files)
            {
                File.FirstCluster = (uint)Next;
                Next += File.ClusterCount;
            }

            RootCluster = Root.FirstCluster;
            RootClusters = Root.ClusterCount;

            if (Next > ClusterCount + 2)
                throw new InvalidOperationException("ExFAT: not enough clusters");
        }

        private void WriteFat(byte[] Image)
        {
            var Fat = new uint[Math.Max(1, FatLength * BytesPerSector / 4)];
            Fat[0] = 0xFFFFFFF8;
            if (Fat.Length > 1)
                Fat[1] = 0xFFFFFFFF;

            Action<uint, int> Chain = (Start, Count) =>
            {
                for (int i = 0; i < Count; i++)
                    Fat[Start + i] = (i == Count - 1) ? 0xFFFFFFFF : (uint)(Start + i + 1);
            };

            Chain(BitmapCluster, BitmapClusters);
            Chain(UpcaseCluster, UpcaseClusters);
            foreach (var Dir in Directories)
                Chain(Dir.FirstCluster, Dir.ClusterCount);
            foreach (var File in Files)
                Chain(File.FirstCluster, File.ClusterCount);

            long Base = (long)FatOffset * BytesPerSector;
            for (int i = 0; i < Fat.Length; i++)
                ExFatShared.WriteU32(Image, (int)(Base + i * 4), Fat[i]);
        }

        private void WriteUpcase(byte[] Image)
        {
            WriteAtClusters(Image, UpcaseCluster, UpcaseBytes);
        }

        private void WriteBitmap(byte[] Image)
        {
            int Bytes = (ClusterCount + 7) / 8;
            var Bitmap = new byte[Bytes];

            Action<uint, int> Set = (Start, Count) =>
            {
                for (int i = 0; i < Count; i++)
                {
                    uint Cluster = (uint)(Start + i);
                    int Index = (int)(Cluster - 2);
                    if ((Index >= 0) && (Index < ClusterCount))
                        Bitmap[Index / 8] |= (byte)(1 << (Index & 7));
                }
            };

            Set(BitmapCluster, BitmapClusters);
            Set(UpcaseCluster, UpcaseClusters);
            foreach (var Dir in Directories)
                Set(Dir.FirstCluster, Dir.ClusterCount);
            foreach (var File in Files)
                Set(File.FirstCluster, File.ClusterCount);

            WriteAtClusters(Image, BitmapCluster, Bitmap);
        }

        private void WriteDirectories(byte[] Image)
        {
            foreach (var Dir in Directories)
            {
                int Size = Dir.EntryCount * ExFatShared.EntrySize;
                var Data = new byte[Size];
                int Offset = 0;

                if (Dir.Parent == null)
                {
                    WriteVolumeLabel(Data, ref Offset);
                    WriteBitmapEntry(Data, ref Offset);
                    WriteUpcaseEntry(Data, ref Offset);
                }

                foreach (var Child in Dir.Children)
                {
                    var Entry = BuildFileEntry(Child);
                    Array.Copy(Entry, 0, Data, Offset, Entry.Length);
                    Offset += Entry.Length;
                }

                WriteAtClusters(Image, Dir.FirstCluster, Data);
            }
        }

        private void WriteVolumeLabel(byte[] Data, ref int Offset)
        {
            var Entry = new byte[ExFatShared.EntrySize];
            Entry[0] = ExFatShared.EntryLabel;
            Entry[1] = 5;
            string Label = "NYAFS";
            for (int i = 0; i < Label.Length; i++)
                ExFatShared.WriteU16(Entry, 2 + i * 2, Label[i]);
            Array.Copy(Entry, 0, Data, Offset, Entry.Length);
            Offset += Entry.Length;
        }

        private void WriteBitmapEntry(byte[] Data, ref int Offset)
        {
            var Entry = new byte[ExFatShared.EntrySize];
            Entry[0] = ExFatShared.EntryBitmap;
            ExFatShared.WriteU32(Entry, 20, BitmapCluster);
            ExFatShared.WriteU64(Entry, 24, (ulong)((ClusterCount + 7) / 8));
            Array.Copy(Entry, 0, Data, Offset, Entry.Length);
            Offset += Entry.Length;
        }

        private void WriteUpcaseEntry(byte[] Data, ref int Offset)
        {
            var Entry = new byte[ExFatShared.EntrySize];
            Entry[0] = ExFatShared.EntryUpcase;
            ExFatShared.WriteU32(Entry, 4, UpcaseChecksum);
            ExFatShared.WriteU32(Entry, 20, UpcaseCluster);
            ExFatShared.WriteU64(Entry, 24, (ulong)UpcaseBytes.Length);
            Array.Copy(Entry, 0, Data, Offset, Entry.Length);
            Offset += Entry.Length;
        }

        private byte[] BuildFileEntry(Node Node)
        {
            int NameEntries = (Node.Name.Length + 14) / 15;
            int Secondary = 1 + NameEntries;
            var Entries = new byte[(1 + Secondary) * ExFatShared.EntrySize];

            var File = new byte[ExFatShared.EntrySize];
            File[0] = ExFatShared.EntryFile;
            File[1] = (byte)Secondary;
            ExFatShared.WriteU16(File, 4, Node.IsDirectory ? ExFatShared.AttrDirectory : ExFatShared.AttrArchive);
            ExFatShared.WriteU32(File, 8, ExFatShared.DosDateTime(DateTime.Now));
            ExFatShared.WriteU32(File, 12, ExFatShared.DosDateTime(DateTime.Now));
            ExFatShared.WriteU32(File, 16, ExFatShared.DosDateTime(DateTime.Now));
            Array.Copy(File, 0, Entries, 0, File.Length);

            var Stream = new byte[ExFatShared.EntrySize];
            Stream[0] = ExFatShared.EntryStream;
            Stream[1] = (byte)((Node.FirstCluster == 0) ? 0 : 0x03);
            Stream[3] = (byte)Math.Min(255, Node.Name.Length);
            ExFatShared.WriteU16(Stream, 4, ExFatShared.NameHash(Node.Name, Upcase));
            ExFatShared.WriteU64(Stream, 8, (ulong)(Node.IsDirectory ? Node.DirectoryClusters * ClusterSize : Node.DataLength));
            ExFatShared.WriteU32(Stream, 20, Node.FirstCluster);
            ExFatShared.WriteU64(Stream, 24, (ulong)(Node.IsDirectory ? Node.DirectoryClusters * ClusterSize : Node.DataLength));
            Array.Copy(Stream, 0, Entries, ExFatShared.EntrySize, Stream.Length);

            var Names = new List<byte>();
            ExFatShared.WriteName(Names, Node.Name);
            Array.Copy(Names.ToArray(), 0, Entries, ExFatShared.EntrySize * 2, Names.Count);

            ushort Checksum = ExFatShared.EntrySetChecksum(Entries);
            ExFatShared.WriteU16(Entries, 2, Checksum);
            return Entries;
        }

        private void WriteFileData(byte[] Image)
        {
            foreach (var File in Files)
            {
                if ((File.Data != null) && (File.Data.Length > 0))
                    WriteAtClusters(Image, File.FirstCluster, File.Data);
            }
        }

        private void WriteAtClusters(byte[] Image, uint StartCluster, byte[] Data)
        {
            if (StartCluster < 2)
                return;

            long Base = (long)ClusterHeapOffset * BytesPerSector + (long)(StartCluster - 2) * ClusterSize;
            Array.Copy(Data, 0, Image, Base, Math.Min(Data.Length, ClusterSize * Math.Max(1, (Data.Length + ClusterSize - 1) / ClusterSize)));
        }

        private void WriteBoot(byte[] Image)
        {
            int TotalSectors = (int)Math.Min(Int32.MaxValue, TotalBytes / BytesPerSector);
            var Main = new byte[11 * BytesPerSector];
            int Base = 0;

            Main[0] = 0xEB;
            Main[1] = 0x76;
            Main[2] = 0x90;
            ExFatShared.WriteAscii(Main, 3, 8, "EXFAT   ");
            ExFatShared.WriteU64(Main, 64, 0);
            ExFatShared.WriteU64(Main, 72, (ulong)TotalSectors);
            ExFatShared.WriteU32(Main, 80, FatOffset);
            ExFatShared.WriteU32(Main, 84, (uint)FatLength);
            ExFatShared.WriteU32(Main, 88, (uint)ClusterHeapOffset);
            ExFatShared.WriteU32(Main, 92, (uint)ClusterCount);
            ExFatShared.WriteU32(Main, 96, RootCluster);
            ExFatShared.WriteU32(Main, 100, 1);
            ExFatShared.WriteU16(Main, 104, 0x0100);
            ExFatShared.WriteU16(Main, 106, 0);
            Main[108] = 9;
            Main[109] = 3;
            Main[110] = 1;
            Main[111] = 0x80;
            Main[112] = 0;
            Main[510] = 0x55;
            Main[511] = 0xAA;

            for (int Sector = 1; Sector < 9; Sector++)
            {
                Main[Sector * BytesPerSector + 510] = 0x55;
                Main[Sector * BytesPerSector + 511] = 0xAA;
            }

            uint Checksum = ExFatShared.BootChecksum(Main, 0);
            var ChecksumSector = new byte[BytesPerSector];
            for (int i = 0; i < BytesPerSector / 4; i++)
                ExFatShared.WriteU32(ChecksumSector, i * 4, Checksum);

            Array.Copy(Main, 0, Image, 0, Main.Length);
            Array.Copy(ChecksumSector, 0, Image, 11 * BytesPerSector, ChecksumSector.Length);
            Array.Copy(Main, 0, Image, 12 * BytesPerSector, Main.Length);
            Array.Copy(ChecksumSector, 0, Image, 23 * BytesPerSector, ChecksumSector.Length);
        }
    }
}
