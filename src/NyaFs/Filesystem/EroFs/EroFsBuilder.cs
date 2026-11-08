using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NyaFs.Filesystem.EroFs
{
    public class EroFsBuilder : IFilesystemBuilder
    {
        private const int BlockSize = 4096;
        private const int InodeUnit = 32;
        private const int InodeSize = 64;
        private int RootNid = 36;
        private int MetadataStart = 36 * InodeUnit;

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
            public bool BigPclusters;
            public bool Inline;
            public bool Hidden;
            public bool WholeFragment;
            public ulong FragmentOffset;
            public int InlineOffset;
            public int InlineLength;
            public int InlineLcn;
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
            public int PhysicalBlocks;
            public int EncodedLength;
            public byte[] Data;
        }

        private readonly List<Node> Nodes = new List<Node>();
        private Node Root;

        public bool Fragments { get; set; } = false;

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
            PrepareFragments();
            ConfigureMetadata();
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
                    foreach (var Group in N.Groups)
                        NextBlock += Group.PhysicalBlocks;
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
                    int Block = (int)N.StartBlock;
                    foreach (var Group in N.Groups)
                    {
                        Array.Copy(Group.Data, 0, Result, (long)Block * BlockSize, Group.Data.Length);
                        Block += Group.PhysicalBlocks;
                    }
                }
                else if (N.Data.Length > 0)
                {
                    long Offset = (long)N.StartBlock * BlockSize;
                    Array.Copy(N.Data, 0, Result, Offset, N.Data.Length);
                }
            }

            return Result;
        }

        private void PrepareFragments()
        {
            if (!Fragments)
                return;

            var Files = Nodes.Where(N => (N.Type == FilesystemItemType.File) &&
                !N.Hidden && (N.Content != null) && (N.Content.Length > 0)).ToList();
            if (Files.Count == 0)
                return;

            var PackedData = new MemoryStream();
            foreach (var File in Files)
            {
                File.WholeFragment = true;
                File.Compressed = true;
                File.BigPclusters = false;
                File.Inline = false;
                File.Groups.Clear();
                File.FragmentOffset = (ulong)PackedData.Length;
                PackedData.Write(File.Content, 0, File.Content.Length);
            }

            var Packed = new Node
            {
                Path = "/$packed",
                Name = "$packed",
                Type = FilesystemItemType.File,
                Mode = 0x1A4,
                Content = PackedData.ToArray(),
                Hidden = true,
                Nlink = 1
            };

            Nodes.Add(Packed);
        }
        private void ConfigureMetadata()
        {
            RootNid = Nodes.Exists(N => N.Compressed && N.BigPclusters) ? 37 : 36;
            MetadataStart = RootNid * InodeUnit;
        }

        private void PrepareCompression()
        {
            foreach (var N in Nodes)
            {
                N.Compressed = false;
                N.BigPclusters = false;
                N.Inline = false;
                N.Groups.Clear();
                N.TotalLclusters = 0;
                N.MetaSize = InodeSize;

                if ((N.Type != FilesystemItemType.File) || (N.Content == null) || (N.Content.Length < BlockSize))
                    continue;

                int Total = (N.Content.Length + BlockSize - 1) / BlockSize;
                N.TotalLclusters = Total;
                bool AnyCompressed = false;
                bool AnyBig = false;
                int Lcn = 0;

                while (Lcn < Total)
                {
                    int Max = Math.Min(Total - Lcn, 256);
                    int Chosen = 0;
                    byte[] ChosenData = null;
                    int ChosenLength = 0;

                    for (int Count = Max; Count >= 1; Count /= 2)
                    {
                        int Offset = Lcn * BlockSize;
                        int Length = Math.Min(Count * BlockSize, N.Content.Length - Offset);
                        var Compressed = new byte[64 * 1024];

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
                        int PhysicalBlocks = (ChosenLength + BlockSize - 1) / BlockSize;
                        var Block = new byte[PhysicalBlocks * BlockSize];
                        Array.Copy(ChosenData, 0, Block, Block.Length - ChosenLength, ChosenLength);
                        N.Groups.Add(new CompressedGroup
                        {
                            Lcn = Lcn,
                            Count = Chosen,
                            Compressed = true,
                            PhysicalBlocks = PhysicalBlocks,
                            EncodedLength = ChosenLength,
                            Data = Block
                        });
                        AnyCompressed = true;
                        AnyBig |= PhysicalBlocks > 1;
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
                            PhysicalBlocks = 1,
                            Data = Block
                        });
                        Lcn++;
                    }
                }
                if (AnyCompressed)
                {
                    N.Compressed = true;
                    N.BigPclusters = AnyBig;
                    N.MetaSize = AnyBig
                        ? Align(80 + Total * 8, 32)
                        : Align(72 + CompactIndexBytes(Total), 32);
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
                Nodes[i].MetaSize = CalculateMetaSize(Nodes[i], Slot * InodeUnit);
                Slot += Nodes[i].MetaSize / InodeUnit;
            }
        }

        private int CalculateMetaSize(Node Node, int Offset)
        {
            if (Node.WholeFragment)
                return Align(72, 32);

            if (!Node.Compressed)
                return InodeSize;

            int Base = Node.BigPclusters
                ? 80 + Node.TotalLclusters * 8
                : 72 + CompactIndexBytes(Node.TotalLclusters);
            int End = Offset + Base;
            var Last = Node.Groups.Count > 0 ? Node.Groups[Node.Groups.Count - 1] : null;

            if ((Last != null) && Last.Compressed && (Last.EncodedLength > 0) &&
                (Last.EncodedLength <= BlockSize) &&
                ((End % BlockSize) + Last.EncodedLength <= BlockSize))
            {
                Node.Inline = true;
                Node.InlineOffset = End;
                Node.InlineLength = Last.EncodedLength;
                Node.InlineLcn = Last.Lcn;
                Last.PhysicalBlocks = 0;
                return Align(Base + Last.EncodedLength, 32);
            }

            Node.Inline = false;
            return Align(Base, 32);
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
            WriteU16(Data, Sb + 14, (uint)RootNid);
            WriteU64(Data, Sb + 16, (ulong)Nodes.Count);
            WriteU64(Data, Sb + 24, 0);
            WriteU32(Data, Sb + 32, 0);
            WriteU32(Data, Sb + 36, (uint)Blocks);
            WriteU32(Data, Sb + 40, 0);
            WriteU32(Data, Sb + 44, 0);

            var Uuid = new Guid("6e79732d-6572-6f66-732d-696d61676500").ToByteArray();
            Array.Copy(Uuid, 0, Data, Sb + 48, 16);

            bool BigPclusters = Nodes.Exists(N => N.Compressed && N.BigPclusters);
            uint Features = 0;
            if (Nodes.Exists(N => N.Compressed))
                Features |= 1;
            if (BigPclusters)
                Features |= 2;
            if (Nodes.Exists(N => N.Inline))
                Features |= 0x10;
            bool HasFragments = Nodes.Exists(N => N.Hidden);
            if (HasFragments)
                Features |= 0x20;
            WriteU32(Data, Sb + 80, Features);
            WriteU16(Data, Sb + 84, BigPclusters ? 1u : 0u);

            if (BigPclusters)
            {
                int Config = Sb + 128;
                WriteU16(Data, Config, 14u);
                WriteU16(Data, Config + 2, 0xFFFF);
                WriteU16(Data, Config + 4, 16);
            }
            WriteU16(Data, Sb + 86, 0);
            WriteU16(Data, Sb + 88, 0);
            Data[Sb + 90] = 0;
            Data[Sb + 91] = 0;
            WriteU32(Data, Sb + 92, 0);
            WriteU64(Data, Sb + 96, HasFragments ? Nodes.First(N => N.Hidden).Nid : 0);
            Data[Sb + 104] = 0;
            Data[Sb + 105] = 0;
            WriteU32(Data, Sb + 108, 0);
            WriteU64(Data, Sb + 112, 0);
            WriteU64(Data, Sb + 120, 0);
        }

        private void WriteInode(byte[] Image, Node Node, int Index)
        {
            int Offset = (int)(Node.Nid * InodeUnit);

            if (Node.WholeFragment)
            {
                WriteU16(Image, Offset, 3);
                WriteU16(Image, Offset + 2, 0);
                WriteU16(Image, Offset + 4, (uint)((Node.Mode & 0xFFF) | TypeBits(Node.Type)));
                WriteU16(Image, Offset + 6, 0);
                WriteU64(Image, Offset + 8, (ulong)Node.Data.Length);
                WriteU32(Image, Offset + 16, 0);
                WriteU32(Image, Offset + 20, Node.Ino);
                WriteU32(Image, Offset + 24, Node.User);
                WriteU32(Image, Offset + 28, Node.Group);
                WriteU64(Image, Offset + 32, 0);
                WriteU32(Image, Offset + 40, 0);
                WriteU32(Image, Offset + 44, Node.Nlink);
                WriteU64(Image, Offset + InodeSize, Node.FragmentOffset | (1UL << 63));
                return;
            }

            WriteU16(Image, Offset, Node.Compressed ? (Node.BigPclusters ? 3u : 7u) : 1u);
            WriteU16(Image, Offset + 2, 0);
            WriteU16(Image, Offset + 4, (uint)((Node.Mode & 0xFFF) | TypeBits(Node.Type)));
            WriteU16(Image, Offset + 6, 0);
            WriteU64(Image, Offset + 8, (ulong)Node.Data.Length);
            WriteU32(Image, Offset + 16, Node.Compressed ? (uint)Node.Groups.Sum(G => G.PhysicalBlocks) : DataAddress(Node));
            WriteU32(Image, Offset + 20, Node.Ino);
            WriteU32(Image, Offset + 24, Node.User);
            WriteU32(Image, Offset + 28, Node.Group);
            WriteU64(Image, Offset + 32, 0);
            WriteU32(Image, Offset + 40, 0);
            WriteU32(Image, Offset + 44, Node.Nlink);

            if (Node.Compressed)
            {
                int Header = Offset + InodeSize;
                uint Advise = (uint)(Node.BigPclusters ? 2 : 1);
                if (Node.Inline)
                    Advise |= 8;

                WriteU32(Image, Header, 0);
                WriteU16(Image, Header + 4, Advise);
                if (Node.Inline)
                    WriteU16(Image, Header + 2, (uint)Node.InlineLength);
                Image[Header + 6] = 0;
                Image[Header + 7] = 0;

                if (Node.BigPclusters)
                    WriteFullIndexes(Image, Node, Header);
                else
                    WriteCompactIndexes(Image, Node, Header);

                if (Node.Inline)
                {
                    var Last = Node.Groups[Node.Groups.Count - 1];
                    Array.Copy(Last.Data, Last.Data.Length - Node.InlineLength,
                        Image, Node.InlineOffset, Node.InlineLength);
                }
            }
        }

        private void WriteFullIndexes(byte[] Image, Node Node, int Header)
        {
            int EntryBase = Header + 16;
            int BlockOffset = 0;

            foreach (var Group in Node.Groups)
            {
                int EntryOffset = EntryBase + Group.Lcn * 8;
                int Physical = (Node.Inline && (Group.Lcn == Node.InlineLcn)) ? 0 : (int)Node.StartBlock + BlockOffset;

                if (Group.Compressed)
                {
                    WriteU16(Image, EntryOffset, 1);
                    WriteU16(Image, EntryOffset + 2, 0);
                    WriteU32(Image, EntryOffset + 4, (uint)Physical);

                    for (int i = 1; i < Group.Count; i++)
                    {
                        int NonHead = EntryBase + (Group.Lcn + i) * 8;
                        uint Delta0 = (uint)i;
                        if ((i == 1) && (Group.PhysicalBlocks > 1))
                            Delta0 = 0x800u | (uint)Group.PhysicalBlocks;

                        WriteU16(Image, NonHead, 2);
                        WriteU16(Image, NonHead + 2, BlockSize);
                        WriteU16(Image, NonHead + 4, Delta0);
                        WriteU16(Image, NonHead + 6, (uint)(Group.Count - i));
                    }
                }
                else
                {
                    WriteU16(Image, EntryOffset, 0);
                    WriteU16(Image, EntryOffset + 2, 0);
                    WriteU32(Image, EntryOffset + 4, (uint)Physical);
                }

                BlockOffset += Group.PhysicalBlocks;
            }
        }
        private static int CompactIndexBytes(int Total)
        {
            GetCompactLayout(Total, out int Initial, out int TwoB, out int FourBEnd);
            return Initial * 4 + TwoB * 2 + ((FourBEnd + 1) / 2) * 8;
        }

        private static void GetCompactLayout(int Total, out int Initial, out int TwoB, out int FourBEnd)
        {
            if (Total < 6)
            {
                Initial = 0;
                TwoB = 0;
                FourBEnd = Total;
                return;
            }

            Initial = 6;
            TwoB = ((Total - Initial) / 16) * 16;
            FourBEnd = Total - Initial - TwoB;
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
                    Physical[Group.Lcn] = (Node.Inline && (Group.Lcn == Node.InlineLcn)) ? 0 : (int)Node.StartBlock + Block;
                    for (int i = 1; i < Group.Count; i++)
                    {
                        int EntryLcn = Group.Lcn + i;
                        Types[EntryLcn] = 2;
                        Delta0[EntryLcn] = i;
                        Delta1[EntryLcn] = Group.Count - i;
                    }
                }
                else
                {
                    Types[Group.Lcn] = 0;
                    Physical[Group.Lcn] = (Node.Inline && (Group.Lcn == Node.InlineLcn)) ? 0 : (int)Node.StartBlock + Block;
                }

                Block++;
            }

            GetCompactLayout(Total, out int Initial, out int TwoB, out int FourBEnd);

            int PackOffset = Header + 8;
            int Lcn = 0;
            int BlkAddr = (int)Node.StartBlock - 1;
            bool DummyHead = true;

            while (Initial > 0)
            {
                WriteCompactPack(Image, ref PackOffset, Types, Physical, Delta0, Delta1,
                    ref Lcn, 2, 4, false, ref BlkAddr, ref DummyHead);
                Initial -= 2;
            }

            while (TwoB > 0)
            {
                WriteCompactPack(Image, ref PackOffset, Types, Physical, Delta0, Delta1,
                    ref Lcn, 16, 2, false, ref BlkAddr, ref DummyHead);
                TwoB -= 16;
            }

            while (FourBEnd > 1)
            {
                WriteCompactPack(Image, ref PackOffset, Types, Physical, Delta0, Delta1,
                    ref Lcn, 2, 4, false, ref BlkAddr, ref DummyHead);
                FourBEnd -= 2;
            }

            if (FourBEnd > 0)
            {
                WriteCompactPack(Image, ref PackOffset, Types, Physical, Delta0, Delta1,
                    ref Lcn, 2, 4, true, ref BlkAddr, ref DummyHead);
            }
        }

        private void WriteCompactPack(byte[] Image, ref int PackOffset, int[] Types, int[] Physical,
            int[] Delta0, int[] Delta1, ref int Lcn, int Count, int EntrySize, bool Final,
            ref int BlkAddr, ref bool DummyHead)
        {
            int Lobits = 12;
            int EncodeBits = (Count * EntrySize * 8 - 32) / Count;
            int PackSize = Count * EntrySize;
            int Base = BlkAddr;
            int Bit = 0;

            for (int i = 0; i < Count; i++)
            {
                int Value = 0;
                if (Lcn < Types.Length)
                {
                    if (Types[Lcn] == 2)
                    {
                        int Offset = (i + 1 == Count) ? Delta1[Lcn] : Delta0[Lcn];
                        Value = (2 << Lobits) | (Offset & (0x7FF));
                    }
                    else
                    {
                        if (DummyHead)
                            BlkAddr++;

                        DummyHead = true;
                        Value = Types[Lcn] << Lobits;

                        // Physical block addresses are implied by the compact base blkaddr.
                    }

                    Lcn++;
                }
                else if (DummyHead)
                {
                    BlkAddr++;
                }

                WriteBits(Image, PackOffset, ref Bit, Value, EncodeBits);
            }

            WriteU32(Image, PackOffset + PackSize - 4, (uint)Base);
            PackOffset += PackSize;
        }

        private static void WriteBits(byte[] Data, int Offset, ref int Bit, int Value, int Count)
        {
            for (int i = 0; i < Count; i++)
            {
                if ((Value & (1 << i)) != 0)
                {
                    int Pos = Offset + ((Bit + i) / 8);
                    Data[Pos] |= (byte)(1 << ((Bit + i) % 8));
                }
            }
            Bit += Count;
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
