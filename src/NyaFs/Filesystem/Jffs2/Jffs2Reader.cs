using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NyaFs.Filesystem.Jffs2
{
    /// <summary>
    /// JFFS2 reader for raw MTD images. Handles little/big-endian nodes,
    /// dirents, inode versions, uncompressed/zlib/zero/rtime data and the
    /// common special-file inode payloads.
    /// </summary>
    public sealed class Jffs2Reader : IFilesystemReader
    {
        private const ushort JFFS2_MAGIC = 0x1985;
        private const ushort JFFS2_MAGIC_SWAPPED = 0x8519;
        private const ushort JFFS2_NODETYPE_DIRENT = 0xE001;
        private const ushort JFFS2_NODETYPE_INODE = 0xE002;
        private const ushort JFFS2_NODETYPE_CLEANMARKER = 0xE003;
        private const ushort JFFS2_NODETYPE_PADDING = 0xE004;
        private const ushort JFFS2_NODETYPE_SUMMARY = 0xE006;

        private sealed class Dirent
        {
            public uint Pino;
            public uint Ino;
            public uint Version;
            public string Name;
            public byte Type;
            public int Offset;
        }

        private sealed class Inode
        {
            public uint Ino;
            public uint Version;
            public uint Mode;
            public uint Uid;
            public uint Gid;
            public uint Isize;
            public uint Atime;
            public uint Mtime;
            public uint Ctime;
            public uint Offset;
            public uint Csize;
            public uint Dsize;
            public byte Compr;
            public int DataOffset;
        }

        private sealed class Node
        {
            public string Path;
            public Inode Inode;
            public FilesystemItemType Type;
            public string Name;
            public readonly List<Node> Children = new List<Node>();
        }

        private readonly byte[] Data;
        private readonly bool BigEndian;
        private readonly Dictionary<string, Dirent> Dirents = new Dictionary<string, Dirent>(StringComparer.Ordinal);
        private readonly Dictionary<uint, List<Inode>> Inodes = new Dictionary<uint, List<Inode>>();
        private readonly Dictionary<uint, Inode> LatestInode = new Dictionary<uint, Inode>();
        private readonly Dictionary<string, Node> Nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly Dictionary<uint, Node> NodesById = new Dictionary<uint, Node>();
        private readonly Node Root;

        public long VolumeBytes => Data.Length;

        public Jffs2Reader(byte[] Data)
        {
            this.Data = Data ?? throw new ArgumentNullException(nameof(Data));
            if (!IsJffs2(Data))
                throw new InvalidOperationException("Not a JFFS2 image");

            BigEndian = DetectEndian(Data);
            Parse();
            SelectLatest();
            Root = new Node { Path = "", Name = "", Type = FilesystemItemType.Directory, Inode = GetInode(1) };
            Nodes[""] = Root;
            NodesById[1] = Root;
            BuildTree();
        }

        public byte[] Read(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Type == FilesystemItemType.Directory))
                return null;

            return ReadInode(Node.Inode);
        }

        public string ReadLink(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Type != FilesystemItemType.SymLink))
                return null;

            var Raw = ReadInode(Node.Inode);
            return Encoding.UTF8.GetString(Raw);
        }

        public DeviceInfo ReadDevice(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || ((Node.Type != FilesystemItemType.Block) && (Node.Type != FilesystemItemType.Character)))
                return null;

            var Raw = ReadInode(Node.Inode);
            if (Raw.Length >= 4)
                return new DeviceInfo((uint)(Raw[0] | (Raw[1] << 8) | (Raw[2] << 16) | (Raw[3] << 24)), 0);
            if (Raw.Length >= 2)
                return new DeviceInfo((uint)(Raw[0] | (Raw[1] << 8)), 0);
            return new DeviceInfo(0, 0);
        }

        public FilesystemEntry[] ReadDir(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Type != FilesystemItemType.Directory))
                return Array.Empty<FilesystemEntry>();

            var Result = new List<FilesystemEntry>();
            var Parent = Normalize(Path);
            foreach (var Child in Node.Children)
            {
                var H = Child.Inode;
                Result.Add(new FilesystemEntry(
                    Child.Type,
                    Combine(Parent, Child.Name),
                    H?.Uid ?? 0,
                    H?.Gid ?? 0,
                    (H?.Mode ?? 0) & 0xFFF,
                    (Child.Type == FilesystemItemType.Directory) ? 0u : (H?.Isize ?? 0)));
            }
            return Result.ToArray();
        }

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            var H = GetInode(1);
            User = H?.Uid ?? 0;
            Group = H?.Gid ?? 0;
            Mode = (H?.Mode ?? 0x1ED) & 0xFFF;
            return H != null;
        }

        private void Parse()
        {
            for (int Offset = 0; Offset + 12 <= Data.Length;)
            {
                ushort Magic = ReadU16(Offset);
                if (Magic != JFFS2_MAGIC)
                {
                    Offset += 4;
                    continue;
                }

                ushort Type = ReadU16(Offset + 2);
                uint Length = ReadU32(Offset + 4);
                if ((Length < 12) || (Offset + Length > Data.Length))
                {
                    Offset += 4;
                    continue;
                }

                if (Type == JFFS2_NODETYPE_DIRENT)
                {
                    if (Length >= 40)
                    {
                        var D = new Dirent
                        {
                            Pino = ReadU32(Offset + 12),
                            Version = ReadU32(Offset + 16),
                            Ino = ReadU32(Offset + 20),
                            Type = Data[Offset + 29],
                            Offset = Offset
                        };
                        int NameLength = Data[Offset + 28];
                        int NameOffset = Offset + 40;
                        if ((NameLength > 0) && (NameOffset + NameLength <= Offset + Length))
                            D.Name = Encoding.UTF8.GetString(Data, NameOffset, NameLength);
                        if (D.Name.Length > 0)
                        {
                            string Key = D.Pino.ToString() + "/" + D.Name;
                            if (!Dirents.TryGetValue(Key, out Dirent Old) || (D.Version >= Old.Version))
                                Dirents[Key] = D;
                        }
                    }
                }
                else if (Type == JFFS2_NODETYPE_INODE)
                {
                    if (Length >= 68)
                    {
                        var I = new Inode
                        {
                            Ino = ReadU32(Offset + 12),
                            Version = ReadU32(Offset + 16),
                            Mode = ReadU32(Offset + 20),
                            Uid = ReadU16(Offset + 24),
                            Gid = ReadU16(Offset + 26),
                            Isize = ReadU32(Offset + 28),
                            Atime = ReadU32(Offset + 32),
                            Mtime = ReadU32(Offset + 36),
                            Ctime = ReadU32(Offset + 40),
                            Offset = ReadU32(Offset + 44),
                            Csize = ReadU32(Offset + 48),
                            Dsize = ReadU32(Offset + 52),
                            Compr = Data[Offset + 56],
                            DataOffset = Offset + 68
                        };

                        if (!Inodes.TryGetValue(I.Ino, out List<Inode> List))
                            Inodes[I.Ino] = List = new List<Inode>();
                        List.Add(I);
                    }
                }

                Offset += Align((int)Length, 4);
            }
        }

        private void SelectLatest()
        {
            foreach (var Pair in Inodes)
            {
                Inode Best = null;
                foreach (var I in Pair.Value)
                    if ((Best == null) || (I.Version > Best.Version))
                        Best = I;
                if (Best != null)
                    LatestInode[Pair.Key] = Best;
            }
        }

        private void BuildTree()
        {
            var Ordered = new List<Dirent>(Dirents.Values);
            Ordered.Sort((A, B) =>
            {
                int P = A.Pino.CompareTo(B.Pino);
                return (P != 0) ? P : StringComparer.Ordinal.Compare(A.Name, B.Name);
            });

            bool Added;
            do
            {
                Added = false;
                foreach (var D in Ordered)
                {
                    if ((D.Ino == 0) || (D.Pino == 0) || (D.Name.Length == 0))
                        continue;

                    string ParentPath = PathOf(D.Pino);
                    if ((D.Pino != 1) && (ParentPath == null))
                        continue;

                    if (D.Pino == 1)
                        ParentPath = "";
                    else if (!NodesById.TryGetValue(D.Pino, out Node ParentNode))
                        continue;

                    string Path = Combine(ParentPath, D.Name);
                    if (Nodes.ContainsKey(Path))
                        continue;

                    var I = GetInode(D.Ino);
                    var N = new Node
                    {
                        Path = Path,
                        Name = D.Name,
                        Inode = I,
                        Type = TypeOf(I, D.Type)
                    };

                    if (D.Pino == 1)
                        Root.Children.Add(N);
                    else
                        NodesById[D.Pino].Children.Add(N);

                    Nodes[Path] = N;
                    NodesById[D.Ino] = N;
                    Added = true;
                }
            }
            while (Added);
        }

        private string PathOf(uint Ino)
        {
            if (Ino == 1)
                return "";
            return NodesById.TryGetValue(Ino, out Node N) ? N.Path : null;
        }

        private Inode GetInode(uint Ino) =>
            LatestInode.TryGetValue(Ino, out Inode I) ? I : null;

        private Node Find(string Path)
        {
            var Normalized = Normalize(Path);
            return Nodes.TryGetValue(Normalized, out Node N) ? N : null;
        }

        private byte[] ReadInode(Inode Inode)
        {
            if (Inode == null)
                return Array.Empty<byte>();

            var Result = new byte[Inode.Isize];
            if (!Inodes.TryGetValue(Inode.Ino, out List<Inode> List))
                return Result;

            var ByOffset = new Dictionary<uint, Inode>();
            foreach (var I in List)
            {
                if ((I.Csize == 0) && (I.Dsize == 0))
                    continue;
                if (!ByOffset.TryGetValue(I.Offset, out Inode Old) || (I.Version > Old.Version))
                    ByOffset[I.Offset] = I;
            }

            foreach (var I in ByOffset.Values)
            {
                if (I.Offset >= Inode.Isize)
                    continue;

                var Chunk = Decode(I);
                int Dst = (int)Math.Min(I.Offset, Inode.Isize);
                int Len = Math.Min(Chunk.Length, Result.Length - Dst);
                if (Len > 0)
                    Array.Copy(Chunk, 0, Result, Dst, Len);
            }

            return Result;
        }

        private byte[] Decode(Inode I)
        {
            if (I.Csize == 0)
                return Array.Empty<byte>();
            if (I.DataOffset + I.Csize > Data.Length)
                return Array.Empty<byte>();

            var Raw = new byte[I.Csize];
            Array.Copy(Data, I.DataOffset, Raw, 0, Raw.Length);

            switch (I.Compr)
            {
                case 0: // NONE
                case 4: // COPY
                    return Raw;
                case 1: // ZERO
                    return new byte[I.Dsize];
                case 2: // RTIME
                    return RtimeDecompress(Raw, I.Dsize);
                case 6: // ZLIB
                    return ZlibDecompress(Raw);
                default:
                    return Raw.Length == I.Dsize ? Raw : Array.Empty<byte>();
            }
        }

        private byte[] ZlibDecompress(byte[] Raw)
        {
            try
            {
                using (var Input = new MemoryStream(Raw, 2, Raw.Length - 2))
                using (var Deflate = new DeflateStream(Input, CompressionMode.Decompress))
                using (var Output = new MemoryStream())
                {
                    Deflate.CopyTo(Output);
                    return Output.ToArray();
                }
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private byte[] RtimeDecompress(byte[] Raw, uint DestLength)
        {
            var Result = new byte[DestLength];
            var Positions = new ushort[256];
            int OutPos = 0;
            int Pos = 0;

            try
            {
                while (OutPos < Result.Length)
                {
                    byte Value = Raw[Pos++];
                    Result[OutPos++] = Value;
                    int Repeat = Raw[Pos++];
                    int Back = Positions[Value];
                    Positions[Value] = (ushort)OutPos;

                    while (Repeat-- > 0)
                    {
                        Result[OutPos] = Result[Back++];
                        OutPos++;
                        if (OutPos >= Result.Length)
                            break;
                    }
                }
            }
            catch
            {
                // Truncated/corrupt rtime data: return what was decoded.
            }

            return Result;
        }

        private static FilesystemItemType TypeOf(Inode I, byte DirentType)
        {
            if (I != null)
            {
                switch (I.Mode & 0xF000)
                {
                    case 0x4000: return FilesystemItemType.Directory;
                    case 0x8000: return FilesystemItemType.File;
                    case 0xA000: return FilesystemItemType.SymLink;
                    case 0x2000: return FilesystemItemType.Character;
                    case 0x6000: return FilesystemItemType.Block;
                    case 0x1000: return FilesystemItemType.Fifo;
                    case 0xC000: return FilesystemItemType.Socket;
                }
            }

            switch (DirentType)
            {
                case 4: return FilesystemItemType.Directory;
                case 8: return FilesystemItemType.File;
                case 10: return FilesystemItemType.SymLink;
                case 2: return FilesystemItemType.Character;
                case 6: return FilesystemItemType.Block;
                case 1: return FilesystemItemType.Fifo;
                case 12: return FilesystemItemType.Socket;
                default: return FilesystemItemType.Unknown;
            }
        }

        public static bool IsJffs2(byte[] Data)
        {
            if (Data == null || Data.Length < 12)
                return false;

            for (int Offset = 0; (Offset < Data.Length - 12) && (Offset < 0x10000); Offset += 4)
            {
                ushort leMagic = (ushort)(Data[Offset] | (Data[Offset + 1] << 8));
                ushort beMagic = (ushort)((Data[Offset] << 8) | Data[Offset + 1]);
                bool be = (leMagic != JFFS2_MAGIC) && (beMagic == JFFS2_MAGIC);
                if ((leMagic != JFFS2_MAGIC) && !be)
                    continue;

                ushort type = be
                    ? (ushort)((Data[Offset + 2] << 8) | Data[Offset + 3])
                    : (ushort)(Data[Offset + 2] | (Data[Offset + 3] << 8));

                if ((type == JFFS2_NODETYPE_DIRENT) || (type == JFFS2_NODETYPE_INODE) ||
                    (type == JFFS2_NODETYPE_CLEANMARKER) || (type == JFFS2_NODETYPE_PADDING))
                    return true;
            }
            return false;
        }

        private static bool DetectEndian(byte[] Data)
        {
            for (int Offset = 0; (Offset < Data.Length - 12) && (Offset < 0x10000); Offset += 4)
            {
                ushort Magic = (ushort)(Data[Offset] | (Data[Offset + 1] << 8));
                if (Magic == JFFS2_MAGIC)
                    return false;
                Magic = (ushort)((Data[Offset] << 8) | Data[Offset + 1]);
                if (Magic == JFFS2_MAGIC)
                    return true;
            }
            return false;
        }

        private ushort ReadU16(int Offset) =>
            BigEndian ? (ushort)((Data[Offset] << 8) | Data[Offset + 1]) : (ushort)(Data[Offset] | (Data[Offset + 1] << 8));

        private uint ReadU32(int Offset) =>
            BigEndian
                ? (uint)((Data[Offset] << 24) | (Data[Offset + 1] << 16) | (Data[Offset + 2] << 8) | Data[Offset + 3])
                : (uint)(Data[Offset] | (Data[Offset + 1] << 8) | (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));

        private static int Align(int Value, int Alignment) => (Value + Alignment - 1) / Alignment * Alignment;

        private static string Normalize(string Path)
        {
            if (String.IsNullOrEmpty(Path) || (Path == ".") || (Path == "/"))
                return "";
            return Path.Replace('\\', '/').Trim('/');
        }

        private static string Combine(string Parent, string Name) =>
            String.IsNullOrEmpty(Parent) ? Name : Parent + "/" + Name;
    }
}
