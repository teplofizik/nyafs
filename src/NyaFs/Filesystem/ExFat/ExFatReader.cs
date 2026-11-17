using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.ExFat
{
    internal sealed class ExFatReader : IFilesystemReader, IRootFsInfo
    {
        private sealed class Node
        {
            public string Name;
            public string Path;
            public FilesystemItemType Type;
            public uint FirstCluster;
            public long Size;
            public DateTime Modified;
            public readonly List<Node> Children = new List<Node>();
        }

        private readonly byte[] Data;
        private readonly int BytesPerSector;
        private readonly int SectorsPerCluster;
        private readonly int ClusterSize;
        private readonly int FatOffset;
        private readonly int FatLength;
        private readonly int ClusterHeapOffset;
        private readonly int ClusterCount;
        private readonly uint RootCluster;
        private readonly uint[] Fat;
        private readonly Dictionary<string, Node> Nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        private readonly Node Root;

        public long VolumeBytes { get; }

        public ExFatReader(byte[] Data)
        {
            this.Data = Data ?? throw new ArgumentNullException(nameof(Data));

            if (!ExFatShared.IsExFat(Data))
                throw new InvalidOperationException("Not an exFAT image");

            BytesPerSector = 1 << Data[108];
            SectorsPerCluster = 1 << Data[109];
            ClusterSize = BytesPerSector * SectorsPerCluster;
            FatOffset = (int)ExFatShared.ReadU32(Data, 80);
            FatLength = (int)ExFatShared.ReadU32(Data, 84);
            ClusterHeapOffset = (int)ExFatShared.ReadU32(Data, 88);
            ClusterCount = (int)ExFatShared.ReadU32(Data, 92);
            RootCluster = ExFatShared.ReadU32(Data, 96);
            VolumeBytes = (long)ExFatShared.ReadU64(Data, 72) * BytesPerSector;

            Fat = new uint[Math.Max(0, FatLength * BytesPerSector / 4)];
            long FatBase = (long)FatOffset * BytesPerSector;
            for (int i = 0; i < Fat.Length; i++)
                Fat[i] = ExFatShared.ReadU32(Data, checked((int)(FatBase + i * 4)));

            Root = new Node
            {
                Name = "",
                Path = "",
                Type = FilesystemItemType.Directory,
                FirstCluster = RootCluster,
                Size = 0
            };
            Nodes[""] = Root;
            ReadDirectory(Root, 0);
        }

        public byte[] Read(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Type == FilesystemItemType.Directory))
                return null;

            var Result = ReadChain(Node.FirstCluster, Node.Size);
            return Result;
        }

        public string ReadLink(string Path) => null;

        public DeviceInfo ReadDevice(string Path) => null;

        public FilesystemEntry[] ReadDir(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Type != FilesystemItemType.Directory))
                return new FilesystemEntry[0];

            var Result = new List<FilesystemEntry>();
            foreach (var Child in Node.Children)
            {
                var ChildPath = ExFatSharedPath.Combine(ExFatSharedPath.Normalize(Path), Child.Name);
                var Size = (Child.Type == FilesystemItemType.Directory) ? 0u : (uint)Math.Max(0, Child.Size);
                Result.Add(new FilesystemEntry(Child.Type, ChildPath, 0, 0,
                    Child.Type == FilesystemItemType.Directory ? 0x1EDu : 0x1A4u, Size));
            }

            return Result.ToArray();
        }

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            User = 0;
            Group = 0;
            Mode = 0x1ED;
            return true;
        }

        private Node Find(string Path)
        {
            var Normalized = ExFatSharedPath.Normalize(Path);
            return Nodes.TryGetValue(Normalized, out Node Result) ? Result : null;
        }

        private void ReadDirectory(Node Directory, long DataLength)
        {
            var Bytes = ReadChain(Directory.FirstCluster, DataLength);
            int Offset = 0;

            while (Offset + ExFatShared.EntrySize <= Bytes.Length)
            {
                byte Type = Bytes[Offset];
                if (Type == ExFatShared.EntryEnd)
                    break;

                if ((Type & 0x80) == 0)
                {
                    Offset += ExFatShared.EntrySize;
                    continue;
                }

                if (Type == ExFatShared.EntryFile)
                {
                    int Secondary = Bytes[Offset + 1];
                    int Total = (1 + Secondary) * ExFatShared.EntrySize;
                    if (Offset + Total > Bytes.Length)
                        break;

                    var Entry = new byte[Total];
                    Array.Copy(Bytes, Offset, Entry, 0, Total);
                    ParseFileEntry(Directory, Entry);
                    Offset += Total;
                    continue;
                }

                Offset += ExFatShared.EntrySize;
            }
        }

        private void ParseFileEntry(Node Directory, byte[] Entry)
        {
            ushort Attributes = ExFatShared.ReadU16(Entry, 4);
            uint Modified = ExFatShared.ReadU32(Entry, 12);
            int StreamOffset = -1;
            int NameOffset = -1;
            int NameEntries = 0;

            for (int Offset = ExFatShared.EntrySize; Offset + ExFatShared.EntrySize <= Entry.Length; Offset += ExFatShared.EntrySize)
            {
                switch (Entry[Offset])
                {
                    case ExFatShared.EntryStream:
                        StreamOffset = Offset;
                        break;
                    case ExFatShared.EntryName:
                        if (NameOffset < 0)
                            NameOffset = Offset;
                        NameEntries++;
                        break;
                }
            }

            if (StreamOffset < 0)
                return;

            int NameLength = Entry[StreamOffset + 3];
            uint FirstCluster = ExFatShared.ReadU32(Entry, StreamOffset + 20);
            ulong DataLength = ExFatShared.ReadU64(Entry, StreamOffset + 24);
            string Name = NameOffset >= 0
                ? ExFatShared.ReadName(Entry, NameOffset, NameEntries, NameLength)
                : "";

            if (String.IsNullOrEmpty(Name) || (Name == ".") || (Name == ".."))
                return;

            bool IsDirectory = (Attributes & ExFatShared.AttrDirectory) != 0;
            var Node = new Node
            {
                Name = Name,
                Path = ExFatSharedPath.Combine(Directory.Path, Name),
                Type = IsDirectory ? FilesystemItemType.Directory : FilesystemItemType.File,
                FirstCluster = FirstCluster,
                Size = IsDirectory ? 0 : (long)Math.Min(DataLength, (ulong)Int64.MaxValue),
                Modified = ExFatShared.ReadDosDateTime(Modified)
            };

            Directory.Children.Add(Node);
            Nodes[Node.Path] = Node;

            if (IsDirectory)
                ReadDirectory(Node, (long)Math.Min(DataLength, (ulong)Int64.MaxValue));
        }

        private byte[] ReadChain(uint StartCluster, long Length)
        {
            var Result = new List<byte>();
            uint Cluster = StartCluster;
            var Visited = new HashSet<uint>();

            while ((Cluster >= 2) && (Cluster < (uint)(ClusterCount + 2)) && Visited.Add(Cluster))
            {
                long Offset = (long)ClusterHeapOffset * BytesPerSector + (long)(Cluster - 2) * ClusterSize;
                int ToRead = ClusterSize;
                if ((Length > 0) && (Result.Count + ToRead > Length))
                    ToRead = (int)Math.Max(0, Length - Result.Count);

                if ((ToRead > 0) && (Offset >= 0) && (Offset + ToRead <= Data.Length))
                {
                    var Chunk = new byte[ToRead];
                    Array.Copy(Data, Offset, Chunk, 0, ToRead);
                    Result.AddRange(Chunk);
                }

                if ((Length > 0) && (Result.Count >= Length))
                    break;

                if (Cluster >= (uint)Fat.Length)
                    break;

                Cluster = Fat[Cluster];
                if (Cluster == 0xFFFFFFFF)
                    break;
            }

            return Result.ToArray();
        }
    }

    internal static class ExFatSharedPath
    {
        public static string Normalize(string Path)
        {
            if (String.IsNullOrEmpty(Path) || (Path == ".") || (Path == "/"))
                return "";

            return Path.Replace('\\', '/').Trim('/');
        }

        public static string Combine(string Parent, string Name) =>
            String.IsNullOrEmpty(Parent) ? Name : Parent + "/" + Name;
    }
}
