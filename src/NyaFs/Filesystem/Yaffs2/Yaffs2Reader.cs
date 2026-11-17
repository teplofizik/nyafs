using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Yaffs2
{
    /// <summary>
    /// YAFFS2 reader for raw NAND dumps. Geometry is auto-detected from the
    /// common page/OOB layouts; tags are expected in the standard YAFFS2 in-band layout.
    /// </summary>
    public sealed class Yaffs2Reader : IFilesystemReader
    {
        private sealed class Header
        {
            public uint ObjectId;
            public uint ParentId;
            public int ObjectType;
            public uint Sequence;
            public int Index;
            public string Name = "";
            public string Alias = "";
            public uint Mode;
            public uint User;
            public uint Group;
            public uint Size;
            public uint Atime;
            public uint Mtime;
            public uint Ctime;
            public uint Rdev;
            public uint EquivId;
            public FilesystemItemType ItemType;
            public bool Deleted;
        }

        private sealed class DataChunk
        {
            public uint ObjectId;
            public uint ChunkId;
            public uint Sequence;
            public int Offset;
            public int Length;
            public int Index;
        }

        private sealed class Node
        {
            public Header Header;
            public string Path;
            public readonly List<Node> Children = new List<Node>();
        }

        private static readonly int[][] Candidates =
        {
            new[] { 2048, 64 },
            new[] { 4096, 128 },
            new[] { 512, 16 },
            new[] { 8192, 224 },
            new[] { 16384, 448 }
        };

        private readonly byte[] Data;
        private readonly int PageSize;
        private readonly int OobSize;
        private readonly bool BigEndian;
        private readonly int ChunkSize;
        private readonly Dictionary<uint, List<Header>> Headers = new Dictionary<uint, List<Header>>();
        private readonly Dictionary<uint, List<DataChunk>> Chunks = new Dictionary<uint, List<DataChunk>>();
        private readonly Dictionary<uint, Header> Latest = new Dictionary<uint, Header>();
        private readonly Dictionary<string, Node> Nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly Dictionary<uint, Node> NodesById = new Dictionary<uint, Node>();
        private readonly Node Root;

        public long VolumeBytes => Data.Length;

        public Yaffs2Reader(byte[] Data)
        {
            this.Data = Data ?? throw new ArgumentNullException(nameof(Data));
            if (!DetectFormat(Data, out PageSize, out OobSize, out BigEndian))
                throw new InvalidOperationException("Not a YAFFS2 image");

            ChunkSize = PageSize + OobSize;
            Parse();
            BuildLatest();
            Root = new Node { Header = null, Path = "", };
            Nodes[""] = Root;
            NodesById[1] = Root;
            BuildTree();
        }

        public byte[] Read(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Header == null) || (Node.Header.ItemType == FilesystemItemType.Directory))
                return null;

            return ReadNode(Node.Header);
        }

        public string ReadLink(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Header == null) || (Node.Header.ItemType != FilesystemItemType.SymLink))
                return null;

            return Node.Header.Alias;
        }

        public DeviceInfo ReadDevice(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Header == null) ||
                ((Node.Header.ItemType != FilesystemItemType.Block) && (Node.Header.ItemType != FilesystemItemType.Character)))
                return null;

            uint Major = (Node.Header.Rdev >> 8) & 0xFF;
            uint Minor = Node.Header.Rdev & 0xFF;
            return new DeviceInfo(Major, Minor);
        }

        public FilesystemEntry[] ReadDir(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || ((Node.Header != null) && (Node.Header.ItemType != FilesystemItemType.Directory)))
                return Array.Empty<FilesystemEntry>();

            var Result = new List<FilesystemEntry>();
            var Parent = ExFatPath.Normalize(Path);
            foreach (var Child in Node.Children)
            {
                var H = Child.Header;
                Result.Add(new FilesystemEntry(
                    H.ItemType,
                    ExFatPath.Combine(Parent, H.Name),
                    H.User,
                    H.Group,
                    H.Mode & 0xFFF,
                    (H.ItemType == FilesystemItemType.Directory) ? 0u : H.Size));
            }
            return Result.ToArray();
        }

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            var H = GetRootHeader();
            User = H?.User ?? 0;
            Group = H?.Group ?? 0;
            Mode = H?.Mode & 0xFFF ?? 0x1ED;
            return H != null;
        }

        private void Parse()
        {
            for (int Offset = 0; Offset + ChunkSize <= Data.Length; Offset += ChunkSize)
            {
                int Oob = Offset + PageSize;
                if (Data[Oob + 1] != 0xFF)
                    continue;

                uint Sequence = ReadU32(Oob + 2);
                if ((Sequence < 0x1000) || (Sequence > 0xEFFFFFFF))
                    continue;

                uint RawObjectId = ReadU32(Oob + 6);
                int ObjectType = (int)(RawObjectId >> 28);
                uint ObjectId = RawObjectId & 0x0FFFFFFF;
                uint Chunk = ReadU32(Oob + 10);
                uint NBytes = ReadU16(Oob + 14);
                if ((ObjectId == 0) || (NBytes > PageSize))
                    continue;

                bool PackedHeader = (Chunk & 0x80000000) != 0;
                uint ParentId = PackedHeader ? (Chunk & 0x0FFFFFFF) : 0;
                uint ChunkId = PackedHeader ? 0 : (Chunk & 0x0FFFFFFF);

                if (ChunkId == 0)
                {
                    if (ObjectType < 1 || ObjectType > 5)
                        continue;

                    var H = ParseHeader(Offset, ObjectId, ParentId, ObjectType, Sequence, Offset / ChunkSize);
                    if (H == null)
                        continue;

                    if (!Headers.TryGetValue(ObjectId, out List<Header> List))
                        Headers[ObjectId] = List = new List<Header>();
                    List.Add(H);
                }
                else
                {
                    if (ChunkId == 0x0FFFFFFF)
                        continue;

                    if (!Chunks.TryGetValue(ObjectId, out List<DataChunk> List))
                        Chunks[ObjectId] = List = new List<DataChunk>();
                    List.Add(new DataChunk
                    {
                        ObjectId = ObjectId,
                        ChunkId = ChunkId,
                        Sequence = Sequence,
                        Offset = Offset,
                        Length = (int)Math.Min(NBytes, (uint)PageSize),
                        Index = Offset / ChunkSize
                    });
                }
            }
        }

        private Header ParseHeader(int Offset, uint ObjectId, uint ParentId, int ObjectType, uint Sequence, int Index)
        {
            string Name = ReadString(Offset + 10, 254);
            if (Name.Length == 0 && ObjectId != 1)
                return null;

            uint Mode = ReadU32(Offset + 268);
            uint SizeLow = ReadU32(Offset + 292);
            uint SizeHigh = ReadU32(Offset + 496);
            uint Size = (SizeHigh != 0xFFFFFFFF) ? (SizeLow | (SizeHigh << 32)) : (SizeLow != 0xFFFFFFFF ? SizeLow : 0);
            uint AliasOffset = 300;
            string Alias = ReadString(Offset + (int)AliasOffset, 160);

            var H = new Header
            {
                ObjectId = ObjectId,
                ParentId = ParentId,
                ObjectType = ObjectType,
                Sequence = Sequence,
                Index = Index,
                Name = Name,
                Alias = Alias,
                Mode = Mode,
                User = ReadU32(Offset + 272),
                Group = ReadU32(Offset + 276),
                Atime = ReadU32(Offset + 280),
                Mtime = ReadU32(Offset + 284),
                Ctime = ReadU32(Offset + 288),
                Size = Size,
                EquivId = ReadU32(Offset + 296),
                Rdev = ReadU32(Offset + 460)
            };

            H.ItemType = TypeOf(Mode, ObjectType);
            H.Deleted = (ParentId > 1) && (ParentId <= 4);
            return H;
        }

        private void BuildLatest()
        {
            foreach (var Pair in Headers)
            {
                Header Best = null;
                foreach (var H in Pair.Value)
                {
                    if ((Best == null) || (H.Sequence > Best.Sequence) ||
                        ((H.Sequence == Best.Sequence) && (H.Index > Best.Index)))
                        Best = H;
                }
                if (Best != null)
                    Latest[Pair.Key] = Best;
            }
        }

        private void BuildTree()
        {
            var Ordered = new List<Header>(Latest.Values);
            Ordered.Sort((A, B) => A.ObjectId.CompareTo(B.ObjectId));

            foreach (var H in Ordered)
            {
                if (H.Deleted || (H.Name.Length == 0) || (H.ObjectId == 1))
                    continue;

                if ((H.ItemType == FilesystemItemType.HardLink) || (H.ParentId == 0) || (H.ParentId > 0x0FFFFFFF))
                    continue;

                if (H.ParentId == 1)
                {
                    AddNode(Root, H);
                    continue;
                }

                if (!Latest.TryGetValue(H.ParentId, out Header Parent) ||
                    (Parent.ItemType != FilesystemItemType.Directory) || Parent.Deleted)
                    continue;

                if (!NodesById.TryGetValue(H.ParentId, out Node ParentNode))
                    continue;

                AddNode(ParentNode, H);
            }
        }

        private void AddNode(Node Parent, Header H)
        {
            string Path = ExFatPath.Combine(Parent.Path, H.Name);
            if (Nodes.ContainsKey(Path))
                return;

            var Node = new Node { Header = H, Path = Path };
            Parent.Children.Add(Node);
            Nodes[Path] = Node;
            NodesById[H.ObjectId] = Node;
        }

        private Node Find(string Path)
        {
            var Normalized = ExFatPath.Normalize(Path);
            return Nodes.TryGetValue(Normalized, out Node Result) ? Result : null;
        }

        private byte[] ReadNode(Header H)
        {
            if (H.Size == 0)
                return Array.Empty<byte>();

            var Result = new byte[(int)Math.Min(H.Size, (uint)Int32.MaxValue)];
            if (!Chunks.TryGetValue(H.ObjectId, out List<DataChunk> List))
                return Result;

            var Selected = new Dictionary<uint, DataChunk>();
            foreach (var C in List)
            {
                if (C.Sequence > H.Sequence)
                    continue;

                if (!Selected.TryGetValue(C.ChunkId, out DataChunk Old) ||
                    (C.Sequence > Old.Sequence) ||
                    ((C.Sequence == Old.Sequence) && (C.Index > Old.Index)))
                    Selected[C.ChunkId] = C;
            }

            foreach (var C in Selected.Values)
            {
                int Dst = (int)((C.ChunkId - 1) * (uint)PageSize);
                if ((Dst < 0) || (Dst >= Result.Length))
                    continue;

                int Len = Math.Min(C.Length, Result.Length - Dst);
                Array.Copy(Data, C.Offset, Result, Dst, Len);
            }

            return Result;
        }

        private Header GetRootHeader()
        {
            if (Latest.TryGetValue(1, out Header H))
                return H;
            return null;
        }

        private string ReadString(int Offset, int MaxLength)
        {
            int End = Offset;
            int Limit = Offset + MaxLength;
            while ((End < Limit) && (End < Data.Length) && (Data[End] != 0))
                End++;

            return Encoding.UTF8.GetString(Data, Offset, End - Offset);
        }

        private FilesystemItemType TypeOf(uint Mode, int ObjectType)
        {
            switch (Mode & 0xF000)
            {
                case 0x4000: return FilesystemItemType.Directory;
                case 0x8000: return FilesystemItemType.File;
                case 0xA000: return FilesystemItemType.SymLink;
                case 0x2000: return FilesystemItemType.Character;
                case 0x6000: return FilesystemItemType.Block;
                case 0x1000: return FilesystemItemType.Fifo;
                case 0xC000: return FilesystemItemType.Socket;
            }

            switch (ObjectType)
            {
                case 1: return FilesystemItemType.File;
                case 2: return FilesystemItemType.SymLink;
                case 3: return FilesystemItemType.Directory;
                case 4: return FilesystemItemType.HardLink;
                case 5: return FilesystemItemType.Character;
                default: return FilesystemItemType.Unknown;
            }
        }

        public static bool IsYaffs2(byte[] Data) => DetectFormat(Data, out _, out _, out _);

        private static bool DetectFormat(byte[] Data, out int PageSize, out int OobSize, out bool BigEndian)
        {
            PageSize = 0;
            OobSize = 0;
            BigEndian = false;
            int BestScore = 0;
            int BestPlausible = 0;
            int BestHeaders = 0;

            foreach (var Candidate in Candidates)
            {
                int Page = Candidate[0];
                int Oob = Candidate[1];
                int Total = Page + Oob;
                if ((Data.Length < Total * 4) || ((Data.Length % Total) != 0))
                    continue;

                foreach (var Endian in new[] { false, true })
                {
                    int Score = 0;
                    int Plausible = 0;
                    int Headers = 0;
                    int Count = Math.Min(Data.Length / Total, 64);

                    for (int Index = 0; Index < Count; Index++)
                    {
                        int Offset = Index * Total;
                        int OobOffset = Offset + Page;
                        if (Data[OobOffset + 1] != 0xFF)
                            continue;

                        uint Sequence = ReadU32(Data, OobOffset + 2, Endian);
                        uint RawObjectId = ReadU32(Data, OobOffset + 6, Endian);
                        uint ObjectId = RawObjectId & 0x0FFFFFFF;
                        uint Chunk = ReadU32(Data, OobOffset + 10, Endian);
                        uint NBytes = ReadU16(Data, OobOffset + 14, Endian);
                        int ObjectType = (int)(RawObjectId >> 28);

                        if ((Sequence < 0x1000) || (Sequence > 0xEFFFFFFF) ||
                            (ObjectId == 0) || (ObjectId >= 100000) || (NBytes > Page) ||
                            (ObjectType < 1) || (ObjectType > 5))
                            continue;

                        Plausible++;
                        Score += 2;

                        if ((Chunk & 0x80000000) != 0)
                        {
                            int NameOffset = Offset + 10;
                            int NameLength = 0;
                            while ((NameLength < 254) && (Data[NameOffset + NameLength] != 0))
                                NameLength++;
                            uint Mode = ReadU32(Data, Offset + 268, Endian);
                            if ((NameLength > 0) && ((Mode & 0xF000) != 0))
                            {
                                Headers++;
                                Score += 2;
                            }
                        }
                    }

                    if ((Plausible > BestPlausible) ||
                        ((Plausible == BestPlausible) && (Score > BestScore)))
                    {
                        BestScore = Score;
                        BestPlausible = Plausible;
                        BestHeaders = Headers;
                        PageSize = Page;
                        OobSize = Oob;
                        BigEndian = Endian;
                    }
                }
            }

            return (BestPlausible >= 4) && (BestHeaders >= 1);
        }

        private static uint ReadU16(byte[] Data, int Offset, bool BigEndian = false) =>
            BigEndian ? (uint)((Data[Offset] << 8) | Data[Offset + 1]) : (uint)(Data[Offset] | (Data[Offset + 1] << 8));

        private uint ReadU16(int Offset) => ReadU16(Data, Offset);

        private static uint ReadU32(byte[] Data, int Offset, bool BigEndian = false)
        {
            if (BigEndian)
                return (uint)((Data[Offset] << 24) | (Data[Offset + 1] << 16) | (Data[Offset + 2] << 8) | Data[Offset + 3]);
            return (uint)(Data[Offset] | (Data[Offset + 1] << 8) | (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));
        }

        private uint ReadU32(int Offset) => ReadU32(Data, Offset, BigEndian);

        private static class ExFatPath
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
}
