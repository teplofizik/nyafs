using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Jffs2
{
    /// <summary>
    /// Basic JFFS2 writer: one inode version per object, no compression,
    /// no garbage collection and no cleanmarker/bad-block handling.
    /// </summary>
    public sealed class Jffs2Builder : IFilesystemBuilder
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
            public readonly List<Node> Children = new List<Node>();
        }

        private const ushort MAGIC = 0x1985;
        private const ushort DIRENT = 0xE001;
        private const ushort INODE = 0xE002;

        private readonly int EraseBlockSize;
        private readonly List<Node> Nodes = new List<Node>();
        private readonly Node Root;
        private uint NextIno = 2;
        private uint NextVersion = 1;
        private List<byte> Image;

        public Jffs2Builder(int EraseBlockSize = 0x10000)
        {
            this.EraseBlockSize = EraseBlockSize;
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

            int Size = Align(Image.Count, EraseBlockSize);
            while (Image.Count < Size)
                Image.Add(0xFF);

            return Image.ToArray();
        }

        private void WriteDirent(Node N)
        {
            var Name = Encoding.UTF8.GetBytes(N.Name);
            int Length = Align(40 + Name.Length, 4);
            int Offset = Image.Count;

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

            int Length = Align(68 + Payload.Length, 4);
            int Offset = Image.Count;

            WriteU16(Image, MAGIC);
            WriteU16(Image, INODE);
            WriteU32(Image, (uint)Length);
            WriteU32(Image, 0);
            WriteU32(Image, N.Ino);
            WriteU32(Image, 1);
            WriteU32(Image, ModeValue(N));
            WriteU16(Image, N.User & 0xFFFF);
            WriteU16(Image, N.Group & 0xFFFF);
            WriteU32(Image, (uint)Payload.Length);
            uint NowValue = Now();
            WriteU32(Image, NowValue);
            WriteU32(Image, NowValue);
            WriteU32(Image, NowValue);
            WriteU32(Image, 0);
            WriteU32(Image, (uint)Payload.Length);
            WriteU32(Image, (uint)Payload.Length);
            Image.Add(0); // compression NONE
            Image.Add(0);
            WriteU16(Image, 0);
            WriteU32(Image, 0); // data_crc
            WriteU32(Image, 0); // node_crc
            Image.AddRange(Payload);
            while ((Image.Count - Offset) < Length)
                Image.Add(0);

            var Data = Image.ToArray();
            uint HdrCrc = Crc(Data, Offset, 8);
            WriteU32At(Image, Offset + 8, HdrCrc);
            Data = Image.ToArray();
            uint NodeCrc = Crc(Data, Offset, 60);
            uint DataCrc = Crc(Data, Offset + 68, Payload.Length);
            WriteU32At(Image, Offset + 60, DataCrc);
            WriteU32At(Image, Offset + 64, NodeCrc);
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
