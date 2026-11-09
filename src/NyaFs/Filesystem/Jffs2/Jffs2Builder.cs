using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NyaFs.Filesystem.Jffs2
{
    public enum Jffs2Compression
    {
        None,
        Zlib,
        Rtime,
        Lzo,
        Priority
    }

    /// <summary>
    /// Basic JFFS2 writer: one inode version per object, no compression,
    /// no garbage collection and no cleanmarker/bad-block handling.
    /// </summary>
    public sealed class Jffs2Builder : IFilesystemBuilder, IXattrBuilder
    {
        private sealed class Node
        {
            public string Path;
            public string Name;
            public FilesystemItemType Type;
            public uint User;
            public uint Group;
            public uint Mode;
            public byte[] Content;
            public string Target;
            public uint Major;
            public uint Minor;
            public uint ParentId;
            public uint Ino;
            public readonly Dictionary<string, byte[]> Xattrs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public readonly List<Node> Children = new List<Node>();
        }

        private const ushort MAGIC = 0x1985;
        private const ushort DIRENT = 0xE001;
        private const ushort INODE = 0xE002;
        private const ushort CLEANMARKER = 0x2003;
        private const ushort XATTR = 0xE008;
        private const ushort XREF = 0xE009;

        private readonly int EraseBlockSize;
        private readonly List<Node> Nodes = new List<Node>();
        private readonly Node Root;
        private uint NextIno = 2;
        private uint NextVersion = 1;
        private uint NextInodeVersion = 1;
        private uint NextXid = 1;
        private uint NextXseq = 1;
        private List<byte> Image;

        public Jffs2Compression Compression { get; set; } = Jffs2Compression.None;
        public int PageSize { get; set; } = 4096;
        public bool Cleanmarkers { get; set; } = true;

        public Jffs2Builder(int EraseBlockSize = 0x10000, int PageSize = 4096, Jffs2Compression Compression = Jffs2Compression.None)
        {
            this.EraseBlockSize = EraseBlockSize;
            this.PageSize = PageSize;
            this.Compression = Compression;
            Root = new Node
            {
                Path = "/",
                Name = "",
                Type = FilesystemItemType.Directory,
                Mode = 0x1ED,
                Ino = 1
            };
            Nodes.Add(Root);
        }

        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Block, User, Group, Mode, null, null, Major, Minor);

        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Character, User, Group, Mode, null, null, Major, Minor);

        public void Directory(string Path, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Directory, User, Group, Mode, null, null, 0, 0);

        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.File, User, Group, Mode, Content ?? Array.Empty<byte>(), null, 0, 0);

        public void Fifo(string Path, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Fifo, User, Group, Mode, null, null, 0, 0);

        public void Socket(string Path, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Socket, User, Group, Mode, null, null, 0, 0);

        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.SymLink, User, Group, Mode, null, Target ?? "", 0, 0);

        public void Xattr(string Path, string Name, byte[] Value)
        {
            var N = Find(Normalize(Path));
            if (N != null)
                N.Xattrs[Name] = Value ?? Array.Empty<byte>();
        }

        private void Add(string Path, FilesystemItemType Type, uint User, uint Group, uint Mode,
            byte[] Content, string Target, uint Major, uint Minor)
        {
            string Normalized = Normalize(Path);
            if (Normalized == "/")
            {
                Root.User = User;
                Root.Group = Group;
                Root.Mode = Mode & 0xFFF;
                return;
            }

            Node Parent = Find(ParentOf(Normalized));
            if (Parent == null || Parent.Type != FilesystemItemType.Directory)
                throw new InvalidOperationException($"JFFS2: parent of {Normalized} is not a directory");

            string Name = NameOf(Normalized);
            if (Parent.Children.Exists(N => N.Name == Name))
                throw new InvalidOperationException($"JFFS2: entry {Normalized} already exists");

            var Node = new Node
            {
                Path = Normalized,
                Name = Name,
                Type = Type,
                User = User,
                Group = Group,
                Mode = Mode & 0xFFF,
                Content = Content,
                Target = Target,
                Major = Major,
                Minor = Minor,
                ParentId = Parent.Ino,
                Ino = NextIno++
            };

            Parent.Children.Add(Node);
            Nodes.Add(Node);
        }

        private Node Find(string Path)
        {
            string Normalized = Normalize(Path);
            foreach (var N in Nodes)
                if (N.Path == Normalized)
                    return N;
            return null;
        }

        public byte[] GetFilesystemImage()
        {
            Image = new List<byte>();
            WriteInode(Root);

            foreach (var N in Nodes)
            {
                if (N == Root)
                    continue;

                WriteDirent(N);
                WriteInode(N);
            }

            WriteXattrs();

            int Size = Align(Image.Count, EraseBlockSize);
            while (Image.Count < Size)
                Image.Add(0xFF);

            return Image.ToArray();
        }

        private void WriteDirent(Node N)
        {
            var Name = Encoding.UTF8.GetBytes(N.Name);
            int Length = Align(40 + Name.Length, 4);
            int Offset = EnsureNodeSpace(Length);

            WriteU16(Image, MAGIC);
            WriteU16(Image, DIRENT);
            WriteU32(Image, (uint)Length);
            WriteU32(Image, 0);
            WriteU32(Image, N.ParentId);
            WriteU32(Image, NextVersion++);
            WriteU32(Image, N.Ino);
            WriteU32(Image, Now());
            Image.Add((byte)Name.Length);
            Image.Add(DirentType(N.Type));
            Image.Add(0); Image.Add(0);
            WriteU32(Image, 0); // node_crc
            WriteU32(Image, 0); // name_crc
            Image.AddRange(Name);
            while ((Image.Count - Offset) < Length)
                Image.Add(0);

            var Data = Image.ToArray();
            uint HdrCrc = Crc(Data, Offset, 8);
            WriteU32At(Image, Offset + 8, HdrCrc);
            Data = Image.ToArray();
            uint NodeCrc = Crc(Data, Offset, 32);
            uint NameCrc = Crc(Data, Offset + 40, Name.Length);
            WriteU32At(Image, Offset + 32, NodeCrc);
            WriteU32At(Image, Offset + 36, NameCrc);
        }

        private void WriteInode(Node N)
        {
            byte[] Payload = N.Type == FilesystemItemType.SymLink
                ? Encoding.UTF8.GetBytes(N.Target ?? "")
                : (N.Type == FilesystemItemType.File ? (N.Content ?? Array.Empty<byte>()) : SpecialPayload(N));

            if (Payload.Length == 0)
            {
                WriteInodeNode(N, 0, 0, 0, Array.Empty<byte>(), 0);
                return;
            }

            for (int Offset = 0; Offset < Payload.Length; Offset += PageSize)
            {
                int DataLength = Math.Min(PageSize, Payload.Length - Offset);
                var Chunk = new byte[DataLength];
                Array.Copy(Payload, Offset, Chunk, 0, DataLength);

                byte[] Stored;
                byte Compr;
                Compress(Chunk, out Stored, out Compr);
                WriteInodeNode(N, Offset, (uint)Payload.Length, DataLength, Stored, Compr);
            }
        }

        private int EnsureNodeSpace(int Length)
        {
            if (Cleanmarkers && (Image.Count % EraseBlockSize) == 0)
                WriteCleanMarker();

            int End = Image.Count % EraseBlockSize + Length;
            if (End > EraseBlockSize)
            {
                int Next = ((Image.Count / EraseBlockSize) + 1) * EraseBlockSize;
                while (Image.Count < Next)
                    Image.Add(0xFF);
                if (Cleanmarkers)
                    WriteCleanMarker();
            }

            return Image.Count;
        }

        private void WriteCleanMarker()
        {
            int Offset = Image.Count;
            WriteU16(Image, MAGIC);
            WriteU16(Image, CLEANMARKER);
            WriteU32(Image, 12);
            WriteU32(Image, 0);
            var Data = Image.ToArray();
            WriteU32At(Image, Offset + 8, Crc(Data, Offset, 8));
        }

        private void WriteInodeNode(Node N, int Offset, uint Isize, int DataLength, byte[] Stored, byte Compr)
        {
            int Length = Align(68 + Stored.Length, 4);
            int NodeOffset = EnsureNodeSpace(Length);

            WriteU16(Image, MAGIC);
            WriteU16(Image, INODE);
            WriteU32(Image, (uint)Length);
            WriteU32(Image, 0);
            WriteU32(Image, N.Ino);
            WriteU32(Image, NextInodeVersion++);
            WriteU32(Image, ModeValue(N));
            WriteU16(Image, N.User & 0xFFFF);
            WriteU16(Image, N.Group & 0xFFFF);
            WriteU32(Image, Isize);
            uint NowValue = Now();
            WriteU32(Image, NowValue);
            WriteU32(Image, NowValue);
            WriteU32(Image, NowValue);
            WriteU32(Image, (uint)Offset);
            WriteU32(Image, (uint)Stored.Length);
            WriteU32(Image, (uint)DataLength);
            Image.Add(Compr);
            Image.Add(0);
            WriteU16(Image, 0);
            WriteU32(Image, 0); // data_crc
            WriteU32(Image, 0); // node_crc
            Image.AddRange(Stored);
            while ((Image.Count - NodeOffset) < Length)
                Image.Add(0);

            var Data = Image.ToArray();
            uint HdrCrc = Crc(Data, NodeOffset, 8);
            WriteU32At(Image, NodeOffset + 8, HdrCrc);
            Data = Image.ToArray();
            uint NodeCrc = Crc(Data, NodeOffset, 60);
            uint DataCrc = Crc(Data, NodeOffset + 68, Stored.Length);
            WriteU32At(Image, NodeOffset + 60, DataCrc);
            WriteU32At(Image, NodeOffset + 64, NodeCrc);
        }

        private void Compress(byte[] Data, out byte[] Stored, out byte Compr)
        {
            Stored = Data;
            Compr = 0;
            if (Compression == Jffs2Compression.None || Data.Length < 128)
                return;

            if (Compression == Jffs2Compression.Priority)
            {
                TryCompression(Data, 6, ZlibCompress, out Stored, out Compr);
                byte[] Best = Stored;
                byte BestType = Compr;
                TryCompression(Data, 2, RtimeCompress, out Stored, out Compr);
                if (Stored.Length < Best.Length) { Best = Stored; BestType = Compr; }
                TryCompression(Data, 7, LzoCompress, out Stored, out Compr);
                if (Stored.Length < Best.Length) { Best = Stored; BestType = Compr; }
                Stored = Best;
                Compr = BestType;
                if (Stored.Length >= Data.Length) { Stored = Data; Compr = 0; }
                return;
            }

            if (Compression == Jffs2Compression.Zlib)
                TryCompression(Data, 6, ZlibCompress, out Stored, out Compr);
            else if (Compression == Jffs2Compression.Rtime)
                TryCompression(Data, 2, RtimeCompress, out Stored, out Compr);
            else if (Compression == Jffs2Compression.Lzo)
                TryCompression(Data, 7, LzoCompress, out Stored, out Compr);
        }

        private static void TryCompression(byte[] Data, byte Type, Func<byte[], byte[]> Compressor,
            out byte[] Stored, out byte Compr)
        {
            Stored = Data;
            Compr = 0;
            try
            {
                var Packed = Compressor(Data);
                if ((Packed != null) && (Packed.Length < Data.Length))
                {
                    Stored = Packed;
                    Compr = Type;
                }
            }
            catch
            {
                // Compression is optional; fall back to an uncompressed node.
            }
        }

        private static byte[] ZlibCompress(byte[] Data)
        {
            using (var Output = new MemoryStream())
            {
                Output.WriteByte(0x78);
                Output.WriteByte(0x9C);
                using (var Deflate = new DeflateStream(Output, CompressionLevel.Optimal, true))
                    Deflate.Write(Data, 0, Data.Length);

                uint Adler = Adler32(Data);
                Output.WriteByte((byte)(Adler >> 24));
                Output.WriteByte((byte)(Adler >> 16));
                Output.WriteByte((byte)(Adler >> 8));
                Output.WriteByte((byte)Adler);
                return Output.ToArray();
            }
        }

        private static uint Adler32(byte[] Data)
        {
            uint A = 1;
            uint B = 0;
            foreach (var V in Data)
            {
                A = (A + V) % 65521;
                B = (B + A) % 65521;
            }
            return (B << 16) | A;
        }

        private static byte[] RtimeCompress(byte[] Data)
        {
            var Positions = new ushort[256];
            var Output = new List<byte>(Data.Length);
            int Pos = 0;
            while (Pos < Data.Length)
            {
                byte Value = Data[Pos++];
                Output.Add(Value);
                int Back = Positions[Value];
                Positions[Value] = (ushort)Pos;
                int Run = 0;
                while ((Back < Pos) && (Pos < Data.Length) && (Data[Pos] == Data[Back++]) && (Run < 255))
                {
                    Pos++;
                    Run++;
                }
                Output.Add((byte)Run);
            }
            return Output.ToArray();
        }

        private static byte[] LzoCompress(byte[] Data) => NyaLZO.LZO1xCompressor.Compress(Data);

        private void WriteXattrs()
        {
            foreach (var N in Nodes)
            {
                foreach (var X in N.Xattrs)
                {
                    uint Xid = NextXid++;
                    WriteXattrNode(Xid, X.Key, X.Value);
                    WriteXrefNode(N.Ino, Xid);
                }
            }
        }

        private void WriteXattrNode(uint Xid, string FullName, byte[] Value)
        {
            byte Prefix;
            string Name;
            if (!SplitXattr(FullName, out Prefix, out Name))
                return;

            var NameBytes = Encoding.UTF8.GetBytes(Name);
            int Length = Align(32 + NameBytes.Length + 1 + Value.Length, 4);
            int Offset = EnsureNodeSpace(Length);

            WriteU16(Image, MAGIC);
            WriteU16(Image, XATTR);
            WriteU32(Image, (uint)Length);
            WriteU32(Image, 0);
            WriteU32(Image, Xid);
            WriteU32(Image, 1);
            Image.Add(Prefix);
            Image.Add((byte)NameBytes.Length);
            WriteU16(Image, (uint)Value.Length);
            WriteU32(Image, 0); // data_crc
            WriteU32(Image, 0); // node_crc
            Image.AddRange(NameBytes);
            Image.Add(0);
            Image.AddRange(Value);
            while ((Image.Count - Offset) < Length)
                Image.Add(0);

            var Data = Image.ToArray();
            uint HdrCrc = Crc(Data, Offset, 8);
            WriteU32At(Image, Offset + 8, HdrCrc);
            Data = Image.ToArray();
            uint DataCrc = Crc(Data, Offset + 32, NameBytes.Length + 1 + Value.Length);
            WriteU32At(Image, Offset + 24, DataCrc);
            Data = Image.ToArray();
            uint NodeCrc = Crc(Data, Offset, 28);
            WriteU32At(Image, Offset + 28, NodeCrc);
        }

        private void WriteXrefNode(uint Ino, uint Xid)
        {
            int Length = 28;
            int Offset = EnsureNodeSpace(Length);

            WriteU16(Image, MAGIC);
            WriteU16(Image, XREF);
            WriteU32(Image, (uint)Length);
            WriteU32(Image, 0);
            WriteU32(Image, Ino);
            WriteU32(Image, Xid);
            WriteU32(Image, NextXseq);
            NextXseq += 2;
            WriteU32(Image, 0); // node_crc

            var Data = Image.ToArray();
            uint HdrCrc = Crc(Data, Offset, 8);
            WriteU32At(Image, Offset + 8, HdrCrc);
            Data = Image.ToArray();
            WriteU32At(Image, Offset + 24, Crc(Data, Offset, 24));
        }

        private static bool SplitXattr(string FullName, out byte Prefix, out string Name)
        {
            Prefix = 0;
            Name = FullName ?? "";
            if (Name.StartsWith("user.", StringComparison.Ordinal)) { Prefix = 1; Name = Name.Substring(5); return true; }
            if (Name.StartsWith("security.", StringComparison.Ordinal)) { Prefix = 2; Name = Name.Substring(9); return true; }
            if (Name.StartsWith("trusted.", StringComparison.Ordinal)) { Prefix = 5; Name = Name.Substring(8); return true; }
            if (Name == "system.posix_acl_access") { Prefix = 3; Name = ""; return true; }
            if (Name == "system.posix_acl_default") { Prefix = 4; Name = ""; return true; }
            return false;
        }

        private byte[] SpecialPayload(Node N)
        {
            if ((N.Type != FilesystemItemType.Block) && (N.Type != FilesystemItemType.Character))
                return Array.Empty<byte>();
            uint Dev = (N.Major << 8) | (N.Minor & 0xFF);
            return new[] { (byte)Dev, (byte)(Dev >> 8) };
        }

        private uint ModeValue(Node N)
        {
            uint Type = N.Type switch
            {
                FilesystemItemType.Directory => 0x4000,
                FilesystemItemType.SymLink => 0xA000,
                FilesystemItemType.Block => 0x6000,
                FilesystemItemType.Character => 0x2000,
                FilesystemItemType.Fifo => 0x1000,
                FilesystemItemType.Socket => 0xC000,
                _ => 0x8000
            };
            return Type | (N.Mode & 0xFFF);
        }

        private static byte DirentType(FilesystemItemType Type)
        {
            switch (Type)
            {
                case FilesystemItemType.Directory: return 4;
                case FilesystemItemType.SymLink: return 10;
                case FilesystemItemType.Block: return 6;
                case FilesystemItemType.Character: return 2;
                case FilesystemItemType.Fifo: return 1;
                case FilesystemItemType.Socket: return 12;
                default: return 8;
            }
        }

        private static uint Now() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static uint Crc(byte[] Data, int Offset, int Length)
        {
            uint Crc = 0;
            for (int i = 0; i < Length; i++)
            {
                Crc ^= Data[Offset + i];
                for (int Bit = 0; Bit < 8; Bit++)
                    Crc = (Crc & 1) != 0 ? (Crc >> 1) ^ 0xEDB88320u : Crc >> 1;
            }
            return Crc;
        }

        private static void WriteU16(List<byte> Data, uint Value)
        {
            Data.Add((byte)Value);
            Data.Add((byte)(Value >> 8));
        }

        private static void WriteU32(List<byte> Data, uint Value)
        {
            Data.Add((byte)Value);
            Data.Add((byte)(Value >> 8));
            Data.Add((byte)(Value >> 16));
            Data.Add((byte)(Value >> 24));
        }

        private static void WriteU32At(List<byte> Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }

        private static void WriteU32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }

        private static int Align(int Value, int Alignment) => (Value + Alignment - 1) / Alignment * Alignment;

        private static string Normalize(string Path)
        {
            if (String.IsNullOrEmpty(Path) || (Path == ".") || (Path == "/"))
                return "/";
            return "/" + Path.Replace('\\', '/').Trim('/');
        }

        private static string NameOf(string Path) => Path.Substring(Path.LastIndexOf('/') + 1);

        private static string ParentOf(string Path)
        {
            int Pos = Path.LastIndexOf('/');
            return Pos <= 0 ? "/" : Path.Substring(0, Pos);
        }
    }
}
