using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Yaffs2
{
    /// <summary>
    /// Minimal YAFFS2 writer for a clean NAND image. It emits one version of every
    /// object and does not implement garbage collection or bad-block management.
    /// </summary>
    public sealed class Yaffs2Builder : IFilesystemBuilder
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
            public uint ObjectId;
            public readonly List<Node> Children = new List<Node>();
        }

        private readonly int PageSize;
        private readonly int OobSize;
        private readonly int ChunkSize;
        private readonly List<Node> Nodes = new List<Node>();
        private readonly Node Root;
        private uint NextObjectId = 0x100;
        private uint NextSequence = 0x1001;
        private int UsedChunks;
        private byte[] Image;

        public Yaffs2Builder(int PageSize = 2048, int OobSize = 64)
        {
            this.PageSize = PageSize;
            this.OobSize = OobSize;
            ChunkSize = PageSize + OobSize;
            Root = new Node
            {
                Path = "/",
                Name = "",
                Type = FilesystemItemType.Directory,
                Mode = 0x1ED,
                ObjectId = 1,
                ParentId = 0
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
                throw new InvalidOperationException($"YAFFS2: parent of {Normalized} is not a directory");

            string Name = NameOf(Normalized);
            if (Parent.Children.Exists(N => N.Name == Name))
                throw new InvalidOperationException($"YAFFS2: entry {Normalized} already exists");

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
                ParentId = Parent.ObjectId,
                ObjectId = NextObjectId++
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
            // Assign IDs to the tree before emitting any chunk.
            UsedChunks = 0;
            Image = new byte[0];

            EmitHeader(Root);
            foreach (var N in Nodes)
            {
                if (N != Root)
                    EmitNode(N);
            }

            int Bytes = Math.Max(UsedChunks, 1) * ChunkSize;
            var Result = new byte[Bytes];
            for (int i = 0; i < Result.Length; i++)
                Result[i] = 0xFF;
            Array.Copy(Image, 0, Result, 0, Math.Min(Image.Length, Result.Length));
            return Result;
        }

        private void EmitNode(Node N)
        {
            // Write the payload before the object header so the header gets the
            // highest sequence number of this object, as expected by the reader.
            if (N.Type == FilesystemItemType.File && N.Content != null)
                EmitData(N);
            EmitHeader(N);
        }

        private void EmitHeader(Node N)
        {
            int Offset = EnsureChunk();
            var Header = new byte[PageSize];
            WriteU32(Header, 0, (uint)ObjectType(N.Type));
            WriteU32(Header, 4, 1);
            WriteString(Header, 10, 254, N.Name);
            WriteU32(Header, 264, 0xFFFFFFFF);
            WriteU32(Header, 268, ModeValue(N));
            WriteU32(Header, 272, N.User);
            WriteU32(Header, 276, N.Group);
            uint Now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteU32(Header, 280, Now);
            WriteU32(Header, 284, Now);
            WriteU32(Header, 288, Now);
            WriteU32(Header, 292, (N.Type == FilesystemItemType.File) ? (uint)(N.Content?.Length ?? 0) : 0);
            WriteU32(Header, 296, 0xFFFFFFFF);
            WriteString(Header, 300, 160, N.Target ?? "");
            WriteU32(Header, 460, EncodeRdev(N));
            WriteU32(Header, 496, 0xFFFFFFFF);

            Array.Copy(Header, 0, Image, Offset, PageSize);
            WriteOob(Offset + PageSize, (uint)(((int)ObjectType(N.Type) << 28) | (N.ObjectId & 0x0FFFFFFF)),
                0x80000000u | N.ParentId, 0);
            UsedChunks++;
        }

        private void EmitData(Node N)
        {
            int Chunk = 1;
            int Remaining = N.Content.Length;
            int Source = 0;
            while (Remaining > 0)
            {
                int Offset = EnsureChunk();
                int Length = Math.Min(Remaining, PageSize);
                Array.Copy(N.Content, Source, Image, Offset, Length);
                WriteOob(Offset + PageSize, N.ObjectId, (uint)Chunk, (uint)Length);
                Source += Length;
                Remaining -= Length;
                Chunk++;
                UsedChunks++;
            }
        }

        private int EnsureChunk()
        {
            int Needed = (UsedChunks + 1) * ChunkSize;
            if (Image.Length < Needed)
            {
                int NewLength = Math.Max(Needed, Math.Max(ChunkSize, Image.Length * 2));
                Array.Resize(ref Image, NewLength);
            }
            return UsedChunks * ChunkSize;
        }

        private void WriteOob(int Offset, uint ObjectId, uint ChunkId, uint NBytes)
        {
            Image[Offset + 1] = 0xFF;
            WriteU32(Image, Offset + 2, NextSequence++);
            WriteU32(Image, Offset + 6, ObjectId);
            WriteU32(Image, Offset + 10, ChunkId);
            WriteU16(Image, Offset + 14, NBytes);
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

        private static int ObjectType(FilesystemItemType Type)
        {
            switch (Type)
            {
                case FilesystemItemType.File: return 1;
                case FilesystemItemType.SymLink: return 2;
                case FilesystemItemType.Directory: return 3;
                case FilesystemItemType.Block:
                case FilesystemItemType.Character:
                case FilesystemItemType.Fifo:
                case FilesystemItemType.Socket: return 5;
                default: return 1;
            }
        }

        private static uint EncodeRdev(Node N) =>
            (N.Major << 8) | (N.Minor & 0xFF);

        private static void WriteString(byte[] Data, int Offset, int MaxLength, string Value)
        {
            var Bytes = Encoding.UTF8.GetBytes(Value ?? "");
            int Length = Math.Min(Bytes.Length, MaxLength - 1);
            Array.Copy(Bytes, 0, Data, Offset, Length);
        }

        private static void WriteU16(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
        }

        private static void WriteU32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }

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
