using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.EroFs
{
    public class EroFsBuilder : IFilesystemBuilder
    {
        private const int BlockSize = 4096;
        private const int InodeUnit = 32;
        private const int InodeSize = 64;
        private const int RootNid = 36;
        private const int MetadataStart = RootNid * InodeUnit;

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
            public Node Parent;
            public readonly List<Node> Children = new List<Node>();
            public ulong Nid;
            public uint Ino;
            public uint Nlink;
            public uint StartBlock;
            public byte[] Data;
            public bool Compressed;
            public int MetaSize = 64;
            public int TotalLclusters;
            public readonly List<CompressedGroup> Groups = new List<CompressedGroup>();
        }

        private sealed class DirEntry
        {
            public byte[] Name;
            public ulong Nid;
            public byte FileType;
        }

        private sealed class CompressedGroup
        {
            public int Lcn;
            public int Count;
            public bool Compressed;
            public byte[] Block;
        }

        private readonly List<Node> Nodes = new List<Node>();
        private Node Root;

        public EroFsBuilder()
        {
            Root = new Node
            {
                Path = "/",
                Name = "",
                Type = FilesystemItemType.Directory,
                Mode = 0x1ED
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
                Root.Mode = Mode;
                return;
            }

            string ParentPath = ParentOf(Normalized);
            string Name = NameOf(Normalized);
            var Parent = Find(ParentPath);
            if ((Parent == null) || (Parent.Type != FilesystemItemType.Directory))
                throw new InvalidOperationException($"EROFS: parent directory for {Normalized} is not found");

            if (Parent.Children.Exists(N => N.Name == Name))
                throw new InvalidOperationException($"EROFS: entry {Normalized} already exists");

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
                Parent = Parent
            };

            Parent.Children.Add(Node);
            Nodes.Add(Node);
        }

        private Node Find(string Path)
        {
            string Normalized = Normalize(Path);
            foreach (var N in Nodes)
            {
                if (N.Path == Normalized)
                    return N;
            }
            return null;
        }

        public byte[] GetFilesystemImage()
        {
            PrepareCompression();
            AssignNids();

            int MetaBytes = MetadataStart;
            foreach (var N in Nodes)
                MetaBytes += N.MetaSize;
            int MetaBlocks = (MetaBytes + BlockSize - 1) / BlockSize;
            int NextBlock = Math.Max(1, MetaBlocks);

            foreach (var N in Nodes)
            {
                if (N.Type == FilesystemItemType.Directory)
                {
                    N.Data = BuildDirectoryData(N);
                    N.Nlink = (uint)(2 + CountSubdirectories(N));
                }
                else if (N.Type == FilesystemItemType.File)
                {
                    N.Data = N.Content ?? Array.Empty<byte>();
                    N.Nlink = 1;
                }
                else if (N.Type == FilesystemItemType.SymLink)
                {
                    N.Data = Encoding.UTF8.GetBytes(N.Target ?? "");
                    N.Nlink = 1;
                }
                else
                {
                    N.Data = Array.Empty<byte>();
                    N.Nlink = 1;
                }

                if (N.Compressed)
                {
                    N.StartBlock = (uint)NextBlock;
                    NextBlock += N.Groups.Count;
                }
                else if (N.Data.Length > 0)
                {
                    N.StartBlock = (uint)NextBlock;
                    NextBlock += (N.Data.Length + BlockSize - 1) / BlockSize;
                }
            }

            var Result = new byte[NextBlock * BlockSize];
            WriteSuperblock(Result, NextBlock);

            for (int i = 0; i < Nodes.Count; i++)
                WriteInode(Result, Nodes[i], i);

            foreach (var N in Nodes)
            {
                if (N.Compressed)
                {
                    for (int i = 0; i < N.Groups.Count; i++)
                        Array.Copy(N.Groups[i].Block, 0, Result, ((long)N.StartBlock + i) * BlockSize, BlockSize);
                }
                else if (N.Data.Length > 0)
                {
                    long Offset = (long)N.StartBlock * BlockSize;
                    Array.Copy(N.Data, 0, Result, Offset, N.Data.Length);
                }
            }

            return Result;
        }

        private void PrepareCompression()
        {
            foreach (var N in Nodes)
            {
                N.Compressed = false;
                N.Groups.Clear();
                N.TotalLclusters = 0;
                N.MetaSize = InodeSize;

                if ((N.Type != FilesystemItemType.File) || (N.Content == null) || (N.Content.Length < BlockSize))
                    continue;

                int Total = (N.Content.Length + BlockSize - 1) / BlockSize;
                N.TotalLclusters = Total;
                bool AnyCompressed = false;
                int Lcn = 0;

                while (Lcn < Total)
                {
                    int Max = Math.Min(Total - Lcn, 128);
                    int Chosen = 0;
                    byte[] ChosenData = null;
                    int ChosenLength = 0;

                    for (int Count = Max; Count >= 1; Count /= 2)
                    {
                        int Offset = Lcn * BlockSize;
                        int Length = Math.Min(Count * BlockSize, N.Content.Length - Offset);
                        var Compressed = new byte[BlockSize];

                        try
                        {
                            int Written = FT.LZ4.LZ4Codec.Encode(N.Content, Offset, Length,
                                Compressed, 0, Compressed.Length, FT.LZ4.LZ4Level.L10_OPT);
                            if ((Written > 0) && (Written < Length))
                            {
                                Chosen = Count;
                                ChosenData = Compressed;
                                ChosenLength = Written;
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (Chosen > 0)
                    {
                        var Block = new byte[BlockSize];
                        Array.Copy(ChosenData, 0, Block, BlockSize - ChosenLength, ChosenLength);
                        N.Groups.Add(new CompressedGroup
                        {
                            Lcn = Lcn,
                            Count = Chosen,
                            Compressed = true,
                            Block = Block
                        });
                        AnyCompressed = true;
                        Lcn += Chosen;
                    }
                    else
                    {
                        var Block = new byte[BlockSize];
                        int Offset = Lcn * BlockSize;
                        int Length = Math.Min(BlockSize, N.Content.Length - Offset);
                        Array.Copy(N.Content, Offset, Block, 0, Length);
                        N.Groups.Add(new CompressedGroup
                        {
                            Lcn = Lcn,
                            Count = 1,
                            Compressed = false,
                            Block = Block
                        });
                        Lcn++;
                    }
                }

                if (AnyCompressed)
                {
                    N.Compressed = true;
                    N.MetaSize = Align(72 + ((Total + 1) / 2) * 8, 32);
                }
                else
                {
                    N.Groups.Clear();
                }
            }
        }
        private void AssignNids()
        {
            int Slot = RootNid;
            for (int i = 0; i < Nodes.Count; i++)
            {
                Nodes[i].Nid = (ulong)Slot;
                Nodes[i].Ino = (uint)(i + 1);
                Slot += Nodes[i].MetaSize / InodeUnit;
            }
        }
        private int CountSubdirectories(Node Dir)
        {
            int Result = 0;
            foreach (var N in Dir.Children)
            {
                if (N.Type == FilesystemItemType.Directory)
                    Result++;
            }
            return Result;
        }

        private byte[] BuildDirectoryData(Node Dir)
        {
            var Entries = new List<DirEntry>();
            AddDirEntry(Entries, ".", Dir.Nid, 2);
            AddDirEntry(Entries, "..", (Dir.Parent ?? Dir).Nid, 2);

            var Children = new List<Node>(Dir.Children);
            Children.Sort((A, B) => StringComparer.Ordinal.Compare(A.Name, B.Name));
            foreach (var Child in Children)
                AddDirEntry(Entries, Child.Name, Child.Nid, FileTypeOf(Child.Type));

            Entries.Sort((A, B) => CompareBytes(A.Name, B.Name));

            var Result = new List<byte>();
            int Index = 0;

            while (Index < Entries.Count)
            {
                int Count = 0;
                int NameBytes = 0;
                while (Index + Count < Entries.Count)
                {
                    int NameLength = Entries[Index + Count].Name.Length;
                    int Candidate = (Count + 1) * 12 + NameBytes + NameLength;
                    if (Candidate > BlockSize)
                        break;

                    Count++;
                    NameBytes += NameLength;
                }

                if (Count == 0)
                    throw new InvalidOperationException("EROFS: directory entry does not fit into a block");

                var Block = new byte[BlockSize];
                int NameOffset = Count * 12;
                for (int i = 0; i < Count; i++)
                {
                    var Entry = Entries[Index + i];
                    int Table = i * 12;
                    WriteU64(Block, Table, Entry.Nid);
                    WriteU16(Block, Table + 8, (uint)(NameOffset));
                    Block[Table + 10] = Entry.FileType;
                    Block[Table + 11] = 0;
                    Array.Copy(Entry.Name, 0, Block, NameOffset, Entry.Name.Length);
                    NameOffset += Entry.Name.Length;
                }

                int Used = NameOffset;
                if (Index + Count < Entries.Count)
                {
                    Result.AddRange(Block);
                }
                else
                {
                    for (int i = 0; i < Used; i++)
                        Result.Add(Block[i]);
                }

                Index += Count;
            }

            return Result.ToArray();
        }

        private static int CompareBytes(byte[] A, byte[] B)
        {
            int Length = Math.Min(A.Length, B.Length);
            for (int i = 0; i < Length; i++)
            {
                int Diff = A[i] - B[i];
                if (Diff != 0)
                    return Diff;
            }
            return A.Length - B.Length;
        }

        private void AddDirEntry(List<DirEntry> Entries, string Name, ulong Nid, byte FileType)
        {
            Entries.Add(new DirEntry
            {
                Name = Encoding.UTF8.GetBytes(Name),
                Nid = Nid,
                FileType = FileType
            });
        }

        private void WriteSuperblock(byte[] Data, int Blocks)
        {
            const int Sb = EroFsShared.SuperblockOffset;

            WriteU32(Data, Sb, EroFsShared.Magic);
            WriteU32(Data, Sb + 4, 0);
            WriteU32(Data, Sb + 8, 0);
            Data[Sb + 12] = 12;
            Data[Sb + 13] = 0;
            WriteU16(Data, Sb + 14, RootNid);
            WriteU64(Data, Sb + 16, (ulong)Nodes.Count);
            WriteU64(Data, Sb + 24, 0);
            WriteU32(Data, Sb + 32, 0);
            WriteU32(Data, Sb + 36, (uint)Blocks);
            WriteU32(Data, Sb + 40, 0);
            WriteU32(Data, Sb + 44, 0);

            var Uuid = new Guid("6e79732d-6572-6f66-732d-696d61676500").ToByteArray();
            Array.Copy(Uuid, 0, Data, Sb + 48, 16);

            WriteU32(Data, Sb + 80, Nodes.Exists(N => N.Compressed) ? 1u : 0u);
            WriteU16(Data, Sb + 84, 0);
            WriteU16(Data, Sb + 86, 0);
            WriteU16(Data, Sb + 88, 0);
            Data[Sb + 90] = 0;
            Data[Sb + 91] = 0;
            WriteU32(Data, Sb + 92, 0);
            WriteU64(Data, Sb + 96, 0);
            Data[Sb + 104] = 0;
            Data[Sb + 105] = 0;
            WriteU32(Data, Sb + 108, 0);
            WriteU64(Data, Sb + 112, 0);
            WriteU64(Data, Sb + 120, 0);
        }

        private void WriteInode(byte[] Image, Node Node, int Index)
        {
            int Offset = (int)(Node.Nid * InodeUnit);

            WriteU16(Image, Offset, Node.Compressed ? 7u : 1u);
            WriteU16(Image, Offset + 2, 0);
            WriteU16(Image, Offset + 4, (uint)((Node.Mode & 0xFFF) | TypeBits(Node.Type)));
            WriteU16(Image, Offset + 6, 0);
            WriteU64(Image, Offset + 8, (ulong)Node.Data.Length);
            WriteU32(Image, Offset + 16, Node.Compressed ? (uint)Node.Groups.Count : DataAddress(Node));
            WriteU32(Image, Offset + 20, Node.Ino);
            WriteU32(Image, Offset + 24, Node.User);
            WriteU32(Image, Offset + 28, Node.Group);
            WriteU64(Image, Offset + 32, 0);
            WriteU32(Image, Offset + 40, 0);
            WriteU32(Image, Offset + 44, Node.Nlink);

            if (Node.Compressed)
            {
                int Header = Offset + InodeSize;
                WriteU32(Image, Header, 0);
                WriteU16(Image, Header + 4, 0);
                Image[Header + 6] = 0;
                Image[Header + 7] = 0;

                WriteCompactIndexes(Image, Node, Header);
            }
        }

        private void WriteCompactIndexes(byte[] Image, Node Node, int Header)
        {
            int Total = Node.TotalLclusters;
            var Types = new int[Total];
            var Physical = new int[Total];
            var Delta0 = new int[Total];
            var Delta1 = new int[Total];

            int Block = 0;
            foreach (var Group in Node.Groups)
            {
                if (Group.Compressed)
                {
                    Types[Group.Lcn] = 1;
                    Physical[Group.Lcn] = (int)Node.StartBlock + Block;
                    for (int i = 1; i < Group.Count; i++)
                    {
                        int Lcn = Group.Lcn + i;
                        Types[Lcn] = 2;
                        Delta0[Lcn] = i;
                        Delta1[Lcn] = Group.Count - i;
                    }
                }
                else
                {
                    Types[Group.Lcn] = 0;
                    Physical[Group.Lcn] = (int)Node.StartBlock + Block;
                }

                Block++;
            }

            int PackStart = Header + 8;
            int BlkAddr = (int)Node.StartBlock - 1;
            bool DummyHead = true;

            for (int Pack = 0; Pack < (Total + 1) / 2; Pack++)
            {
                int Base = BlkAddr;
                for (int i = 0; i < 2; i++)
                {
                    int Index = Pack * 2 + i;
                    int Value = 0;

                    if (Index < Total)
                    {
                        if (Types[Index] == 2)
                        {
                            int Offset = (i == 1) ? Delta1[Index] : Delta0[Index];
                            Value = (2 << 12) | (Offset & 0x7FF);
                        }
                        else
                        {
                            if (DummyHead)
                                BlkAddr++;

                            DummyHead = true;
                            Value = (Types[Index] << 12);
                        }
                    }
                    else
                    {
                        if (DummyHead)
                            BlkAddr++;
                    }

                    WriteU16(Image, PackStart + i * 2, (uint)Value);
                }

                WriteU32(Image, PackStart + 4, (uint)Base);
                PackStart += 8;
            }
        }
        private uint DataAddress(Node Node)
        {
            if ((Node.Type == FilesystemItemType.Character) || (Node.Type == FilesystemItemType.Block))
            {
                uint Minor = Node.Minor & 0xFF;
                uint Major = Node.Major & 0xFFF;
                return Minor | (Major << 8) | ((Node.Minor & 0xFFFFFF00u) << 12);
            }

            return Node.StartBlock;
        }

        private static uint TypeBits(FilesystemItemType Type)
        {
            switch (Type)
            {
                case FilesystemItemType.Fifo: return 0x1000;
                case FilesystemItemType.Character: return 0x2000;
                case FilesystemItemType.Directory: return 0x4000;
                case FilesystemItemType.Block: return 0x6000;
                case FilesystemItemType.File: return 0x8000;
                case FilesystemItemType.SymLink: return 0xA000;
                case FilesystemItemType.Socket: return 0xC000;
                default: return 0;
            }
        }

        private static byte FileTypeOf(FilesystemItemType Type)
        {
            switch (Type)
            {
                case FilesystemItemType.File: return 1;
                case FilesystemItemType.Directory: return 2;
                case FilesystemItemType.Character: return 3;
                case FilesystemItemType.Block: return 4;
                case FilesystemItemType.Fifo: return 5;
                case FilesystemItemType.Socket: return 6;
                case FilesystemItemType.SymLink: return 7;
                default: return 0;
            }
        }

        private static int Align(int Value, int Alignment) =>
            (Value + Alignment - 1) / Alignment * Alignment;

        private static string Normalize(string Path)
        {
            if (String.IsNullOrEmpty(Path) || (Path == ".") || (Path == "/"))
                return "/";

            Path = Path.Replace('\\', '/').Trim('/');
            return "/" + Path;
        }

        private static string NameOf(string Path) =>
            Path.Substring(Path.LastIndexOf('/') + 1);

        private static string ParentOf(string Path)
        {
            int Pos = Path.LastIndexOf('/');
            return (Pos <= 0) ? "/" : Path.Substring(0, Pos);
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

        private static void WriteU64(byte[] Data, int Offset, ulong Value)
        {
            WriteU32(Data, Offset, (uint)Value);
            WriteU32(Data, Offset + 4, (uint)(Value >> 32));
        }
    }
}
