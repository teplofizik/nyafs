using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Udf
{
    public class UdfBuilder : IFilesystemBuilder
    {
        private const int BlockSize = 2048;
        private const int PartitionStart = 257;

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
            public Node Parent;
            public readonly List<Node> Children = new List<Node>();
            public int IcbBlock;
            public int DataBlock;
            public byte[] Data;
            public bool Vat;
        }

        private readonly List<Node> Nodes = new List<Node>();
        private readonly Node Root;
        private readonly bool Vat;
        private readonly bool Sparable;
        private readonly Node VatNode;

        public UdfBuilder(bool Vat = false, bool Sparable = false)
        {
            this.Vat = Vat;
            this.Sparable = Sparable;
            Root = new Node
            {
                Path = "/",
                Name = "",
                Type = FilesystemItemType.Directory,
                Mode = 0x1ED
            };
            Nodes.Add(Root);

            if (Vat)
            {
                VatNode = new Node
                {
                    Path = "/*UDF Virtual Allocation Table",
                    Name = "*UDF Virtual Allocation Table",
                    Type = FilesystemItemType.File,
                    Mode = 0x1A4,
                    Vat = true,
                    Parent = Root
                };
                Root.Children.Add(VatNode);
                Nodes.Add(VatNode);
            }
        }

        public void Directory(string Path, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Directory, User, Group, Mode, null, null);

        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.File, User, Group, Mode, Content ?? Array.Empty<byte>(), null);

        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.SymLink, User, Group, Mode, null, Target ?? "");

        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Block, User, Group, Mode, null, null);

        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Character, User, Group, Mode, null, null);

        public void Fifo(string Path, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Fifo, User, Group, Mode, null, null);

        public void Socket(string Path, uint User, uint Group, uint Mode) =>
            Add(Path, FilesystemItemType.Socket, User, Group, Mode, null, null);

        private void Add(string Path, FilesystemItemType Type, uint User, uint Group, uint Mode,
            byte[] Content, string Target)
        {
            string Normalized = Normalize(Path);
            if (Normalized == "/")
            {
                Root.User = User;
                Root.Group = Group;
                Root.Mode = Mode & 0xFFF;
                return;
            }

            var Parent = Find(ParentOf(Normalized));
            if ((Parent == null) || (Parent.Type != FilesystemItemType.Directory))
                throw new InvalidOperationException($"UDF: parent directory for {Normalized} is not found");

            string Name = NameOf(Normalized);
            if (Parent.Children.Exists(N => N.Name == Name))
                throw new InvalidOperationException($"UDF: entry {Normalized} already exists");

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
                Parent = Parent
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
            if (Vat)
                return GetVatImage();

            int NextIcb = PartitionStart + 2;
            foreach (var N in Nodes)
            {
                N.IcbBlock = NextIcb++;
                N.Data = BuildData(N);
            }

            int NextData = NextIcb;
            foreach (var N in Nodes)
            {
                if (N.Data.Length == 0)
                    continue;

                N.DataBlock = NextData;
                NextData += (N.Data.Length + BlockSize - 1) / BlockSize;
            }

            // Directory data depends on child ICB locations, but not on data block locations.
            foreach (var N in Nodes)
            {
                if (N.Type == FilesystemItemType.Directory)
                    N.Data = BuildDirectoryData(N);
            }
            // Re-run data allocation in case a directory became larger after building.
            NextData = NextIcb;
            foreach (var N in Nodes)
            {
                if (N.Data.Length == 0)
                    continue;
                N.DataBlock = NextData;
                NextData += (N.Data.Length + BlockSize - 1) / BlockSize;
            }

            int Reserve = Math.Max(NextData + 256, 1888);
            int Total = Math.Max(2048, Reserve + 160);
            Reserve = Total - 160;
            int PartitionLength = Reserve - PartitionStart;

            if (NextData >= PartitionStart + PartitionLength)
            {
                Total = NextData + 400;
                Reserve = Total - 160;
                PartitionLength = Reserve - PartitionStart;
            }

            var Image = new byte[Total * BlockSize];
            WriteVrs(Image, 0);
            WriteVolumeDescriptors(Image, 96, Reserve);
            WriteLvid(Image, 128);
            WriteAnchor(Image, 256, 96, Reserve);
            WriteAnchor(Image, Total - 1, 96, Reserve);

            WriteSpaceBitmap(Image, PartitionStart, PartitionLength, NextData - PartitionStart);
            WriteFsd(Image, PartitionStart + 1, Vat);

            foreach (var N in Nodes)
            {
                WriteFe(Image, N);
                if (N.Data.Length > 0)
                    Array.Copy(N.Data, 0, Image, (long)N.DataBlock * BlockSize, N.Data.Length);
            }

            if (Sparable)
                WriteSparableLayout(Image);

            return Image;
        }

        private void WriteSparableLayout(byte[] Image)
        {
            int Lvd = 97 * BlockSize;
            int Map = Lvd + 440;
            int Sparable = Map + 6;
            WriteU32(Image, Lvd + 264, 70);
            WriteU32(Image, Lvd + 268, 2);

            Image[Map] = 1;
            Image[Map + 1] = 6;
            WriteU16(Image, Map + 2, 1);
            WriteU16(Image, Map + 4, 0);

            Image[Sparable] = 2;
            Image[Sparable + 1] = 64;
            WriteRegid(Image, Sparable + 4, "*UDF Sparable Partition");
            WriteU16(Image, Sparable + 28, 0x0201);
            WriteU16(Image, Sparable + 36, 1);
            WriteU16(Image, Sparable + 38, 0);
            WriteU16(Image, Sparable + 40, 32);
            Image[Sparable + 42] = 1;
            WriteU32(Image, Sparable + 44, 56);
            WriteU32(Image, Sparable + 48, 160);

            int Table = 160 * BlockSize;
            WriteTag(Image, Table, 0, 56, 160);
            WriteRegid(Image, Table + 16, "*UDF Sparing Table");
            WriteU16(Image, Table + 44, 0x0201);
            WriteU16(Image, Table + 48, 0);
            WriteU32(Image, Table + 52, 0);
            WriteTag(Image, Table, 0, 56, 160);

            WriteTag(Image, Lvd, 6, 510, 97);
        }

        private byte[] GetVatImage()
        {
            int NextIcb = PartitionStart + 1;
            foreach (var N in Nodes)
            {
                N.IcbBlock = NextIcb++;
                N.Data = BuildData(N);
            }

            int NextData = NextIcb;
            foreach (var N in Nodes)
            {
                if (N.Data.Length == 0)
                    continue;
                N.DataBlock = NextData;
                NextData += (N.Data.Length + BlockSize - 1) / BlockSize;
            }

            VatNode.DataBlock = NextData;
            VatNode.Data = new byte[152];
            NextData += 1;

            foreach (var N in Nodes)
            {
                if (N.Type == FilesystemItemType.Directory)
                    N.Data = BuildDirectoryData(N);
            }

            NextData = NextIcb;
            foreach (var N in Nodes)
            {
                if (N.Data.Length == 0 || N == VatNode)
                    continue;
                N.DataBlock = NextData;
                NextData += (N.Data.Length + BlockSize - 1) / BlockSize;
            }

            int Total = Math.Max(2048, NextData + 64);
            int FeBlock = Total - 1;
            int VatBlocks = 1;
            int VatEntries;
            int VatLength;
            int VatData;
            while (true)
            {
                VatEntries = FeBlock - PartitionStart;
                VatLength = 152 + VatEntries * 4;
                VatBlocks = (VatLength + BlockSize - 1) / BlockSize;
                VatData = FeBlock - VatBlocks;
                if (VatData >= NextData)
                    break;
                Total += (NextData - VatData) + 1;
                FeBlock = Total - 1;
            }

            VatNode.Data = BuildVatTable(VatEntries);
            VatNode.DataBlock = VatData;
            VatNode.IcbBlock = FeBlock;

            foreach (var N in Nodes)
            {
                if (N.Type == FilesystemItemType.Directory)
                    N.Data = BuildDirectoryData(N);
            }

            int Reserve = Total - 160;
            int PartitionLength = FeBlock - PartitionStart + 1;

            var Image = new byte[Total * BlockSize];
            WriteVrs(Image, 0);
            WriteVolumeDescriptors(Image, 96, Reserve, PartitionLength);
            WriteLvid(Image, 128, true);
            WriteAnchor(Image, 256, 96, Reserve);

            WriteFsd(Image, PartitionStart, true);

            foreach (var N in Nodes)
            {
                WriteFe(Image, N);
                if (N.Data.Length > 0)
                    Array.Copy(N.Data, 0, Image, (long)N.DataBlock * BlockSize, N.Data.Length);
            }

            return Image;
        }

        private byte[] BuildVatTable(int Entries)
        {
            var Data = new byte[152 + Entries * 4];
            WriteU16(Data, 0, 152);
            WriteU16(Data, 2, 0);
            WriteDstring(Data, 4, 128, "NyaFS");
            WriteU32(Data, 132, 0xFFFFFFFF);
            WriteU32(Data, 136, (uint)Nodes.Count);
            WriteU32(Data, 140, 1);
            WriteU16(Data, 144, 0x0201);
            WriteU16(Data, 146, 0x0201);
            WriteU16(Data, 148, 0x0201);

            for (int i = 0; i < Entries; i++)
                WriteU32(Data, 152 + i * 4, (uint)i);

            return Data;
        }

        private byte[] BuildData(Node Node)
        {
            switch (Node.Type)
            {
                case FilesystemItemType.Directory:
                    return Array.Empty<byte>();
                case FilesystemItemType.File:
                    return Node.Content ?? Array.Empty<byte>();
                case FilesystemItemType.SymLink:
                    return BuildSymlink(Node.Target);
                default:
                    return Array.Empty<byte>();
            }
        }

        private byte[] BuildSymlink(string Target)
        {
            var Name = Encoding.UTF8.GetBytes(Target ?? "");
            var Data = new byte[4 + 1 + Name.Length];
            Data[0] = 5;
            Data[1] = (byte)(1 + Name.Length);
            Data[2] = 0;
            Data[3] = 0;
            Data[4] = 8;
            Array.Copy(Name, 0, Data, 5, Name.Length);
            return Data;
        }

        private byte[] BuildDirectoryData(Node Dir)
        {
            var Result = new List<byte>();
            AddFid(Result, "", Dir.Parent ?? Dir, 0x0A);

            var Children = new List<Node>(Dir.Children);
            Children.Sort((A, B) => StringComparer.Ordinal.Compare(A.Name, B.Name));
            foreach (var Child in Children)
                AddFid(Result, Child.Name, Child, Child.Vat ? (byte)0x08 : (IsDirectory(Child) ? (byte)0x02 : (byte)0x00));

            return Result.ToArray();
        }

        private void AddFid(List<byte> Result, string Name, Node Child, byte Characteristics)
        {
            var NameData = Encoding.UTF8.GetBytes(Name);
            int NameLength = NameData.Length == 0 ? 0 : NameData.Length + 1;
            int TotalLength = Align(38 + NameLength, 4);
            var Entry = new byte[TotalLength];

            WriteTag(Entry, 0, 257, TotalLength, Child.IcbBlock - PartitionStart);
            WriteU16(Entry, 16, 1);
            Entry[18] = Characteristics;
            Entry[19] = (byte)NameLength;
            WriteLongAd(Entry, 20, 2048, Child.IcbBlock - PartitionStart, Vat ? 1 : 0);
            WriteU16(Entry, 36, 0);

            if (NameLength > 0)
            {
                Entry[38] = 8;
                Array.Copy(NameData, 0, Entry, 39, NameData.Length);
            }
            WriteTag(Entry, 0, 257, TotalLength, Child.IcbBlock - PartitionStart);

            Result.AddRange(Entry);
        }

        private void WriteSpaceBitmap(byte[] Image, int Block, int PartitionLength, int UsedBlocks)
        {
            int Bytes = (PartitionLength + 7) / 8;
            int Length = 24 + Bytes;
            WriteTag(Image, Block * BlockSize, 264, Length, 0);
            WriteU32(Image, Block * BlockSize + 16, (uint)PartitionLength);
            WriteU32(Image, Block * BlockSize + 20, (uint)Bytes);

            for (int i = 0; i < Math.Min(UsedBlocks, PartitionLength); i++)
            {
                int Pos = Block * BlockSize + 24 + i / 8;
                Image[Pos] |= (byte)(1 << (i & 7));
            }
            WriteTag(Image, Block * BlockSize, 264, Length, 0);
        }

        private void WriteVrs(byte[] Image, int Offset)
        {
            WriteVrsEntry(Image, 16 * BlockSize, "BEA01");
            WriteVrsEntry(Image, 17 * BlockSize, "NSR03");
            WriteVrsEntry(Image, 18 * BlockSize, "TEA01");
        }

        private void WriteVrsEntry(byte[] Image, int Offset, string Name)
        {
            Image[Offset] = 0;
            Array.Copy(Encoding.ASCII.GetBytes(Name), 0, Image, Offset + 1, 5);
            Image[Offset + 6] = 1;
        }

        private void WriteVolumeDescriptors(byte[] Image, int Start, int Reserve, int PartitionLength = 0)
        {
            WritePvd(Image, Start);
            WriteLvd(Image, Start + 1);
            WritePd(Image, Start + 2, PartitionLength > 0 ? PartitionLength : Reserve - PartitionStart);
            WriteUsd(Image, Start + 3);
            WriteIuvd(Image, Start + 4);
            WriteTd(Image, Start + 5);
        }

        private void WritePvd(byte[] Image, int Block)
        {
            int B = Block * BlockSize;
            WriteTag(Image, B, 1, 512, Block);
            WriteU32(Image, B + 16, 0);
            WriteU32(Image, B + 20, 0);
            WriteDstring(Image, B + 24, 32, "NyaFS");
            WriteU16(Image, B + 56, 1);
            WriteU16(Image, B + 58, 1);
            WriteU16(Image, B + 60, 2);
            WriteU16(Image, B + 62, 2);
            WriteU32(Image, B + 64, 1);
            WriteU32(Image, B + 68, 1);
            WriteDstring(Image, B + 72, 128, "NyaFS");
            WriteRegid(Image, B + 344, "*NyaFS");
            WriteTimestamp(Image, B + 376);
            WriteRegid(Image, B + 388, "*NyaFS");
            WriteU16(Image, B + 488, 0);
            WriteTag(Image, B, 1, 512, Block);
        }

        private void WriteLvd(byte[] Image, int Block)
        {
            int B = Block * BlockSize;
            int MapLength = Vat ? 70 : 6;
            WriteTag(Image, B, 6, 440 + MapLength, Block);
            WriteU32(Image, B + 16, 0);
            WriteDstring(Image, B + 84, 128, "NyaFS");
            WriteU32(Image, B + 212, BlockSize);
            WriteRegid(Image, B + 216, "*OSTA UDF Compliant");
            WriteLongAd(Image, B + 248, 2048, Vat ? 0 : 1, Vat ? 1 : 0);
            WriteU32(Image, B + 264, (uint)(Vat ? 70 : 6));
            WriteU32(Image, B + 268, (uint)(Vat ? 2 : 1));
            WriteRegid(Image, B + 272, "*NyaFS");
            WriteU32(Image, B + 432, 2048);
            WriteU32(Image, B + 436, 128);

            int Map = B + 440;
            Image[Map] = 1;
            Image[Map + 1] = 6;
            WriteU16(Image, Map + 2, 1);
            WriteU16(Image, Map + 4, 0);

            if (Vat)
            {
                int Virtual = Map + 6;
                Image[Virtual] = 2;
                Image[Virtual + 1] = 64;
                WriteRegid(Image, Virtual + 4, "*UDF Virtual Partition");
                WriteU16(Image, Virtual + 28, 0x0201);
                WriteU16(Image, Virtual + 36, 1);
                WriteU16(Image, Virtual + 38, 0);
            }
            WriteTag(Image, B, 6, 440 + MapLength, Block);
        }

        private void WritePd(byte[] Image, int Block, int PartitionLength)
        {
            int B = Block * BlockSize;
            WriteTag(Image, B, 5, 512, Block);
            WriteU32(Image, B + 16, 0);
            WriteU16(Image, B + 20, 1);
            WriteU16(Image, B + 22, 0);
            WriteRegid(Image, B + 24, "+NSR03");
            WriteU32(Image, B + 184, 4);
            WriteU32(Image, B + 188, (uint)PartitionStart);
            WriteU32(Image, B + 192, (uint)Math.Max(0, PartitionLength));
            WriteRegid(Image, B + 196, "*NyaFS");
            WriteTag(Image, B, 5, 512, Block);
        }

        private void WriteUsd(byte[] Image, int Block)
        {
            int B = Block * BlockSize;
            WriteTag(Image, B, 7, 24, Block);
            WriteU32(Image, B + 16, 0);
            WriteU32(Image, B + 20, 0);
            WriteTag(Image, B, 7, 24, Block);
        }

        private void WriteIuvd(byte[] Image, int Block)
        {
            int B = Block * BlockSize;
            WriteTag(Image, B, 4, 512, Block);
            WriteU32(Image, B + 16, 0);
            WriteRegid(Image, B + 20, "*NyaFS");
            WriteTag(Image, B, 4, 512, Block);
        }

        private void WriteTd(byte[] Image, int Block)
        {
            WriteTag(Image, Block * BlockSize, 8, 16, Block);
        }

        private void WriteLvid(byte[] Image, int Block, bool Vat = false)
        {
            int B = Block * BlockSize;
            WriteTag(Image, B, 9, 80, Block);
            WriteU32(Image, B + 16, Vat ? 0u : 1u);
            WriteU32(Image, B + 24, 0);
            WriteU32(Image, B + 48, 0);
            WriteTag(Image, B, 9, 80, Block);
        }

        private void WriteAnchor(byte[] Image, int Block, int Main, int Reserve)
        {
            int B = Block * BlockSize;
            WriteTag(Image, B, 2, 512, Block);
            WriteU32(Image, B + 16, (uint)(16 * BlockSize));
            WriteU32(Image, B + 20, (uint)Main);
            WriteU32(Image, B + 24, (uint)(16 * BlockSize));
            WriteU32(Image, B + 28, (uint)Reserve);
            WriteTag(Image, B, 2, 512, Block);
        }

        private void WriteFsd(byte[] Image, int Block, bool Vat)
        {
            int B = Block * BlockSize;
            WriteTag(Image, B, 256, 512, Block - PartitionStart);
            WriteTimestamp(Image, B + 16);
            WriteU16(Image, B + 28, 3);
            WriteU16(Image, B + 30, 3);
            WriteU32(Image, B + 32, 1);
            WriteU32(Image, B + 36, 1);
            WriteU32(Image, B + 40, 0);
            WriteU32(Image, B + 44, 0);
            WriteDstring(Image, B + 112, 128, "NyaFS");
            WriteDstring(Image, B + 304, 32, "NyaFS");
            WriteLongAd(Image, B + 400, 2048, Root.IcbBlock - PartitionStart, Vat ? 1 : 0);
            WriteRegid(Image, B + 416, "*OSTA UDF Compliant");
            WriteTag(Image, B, 256, 512, Block - PartitionStart);
        }

        private void WriteFe(byte[] Image, Node Node)
        {
            if (Node.Vat)
            {
                WriteVatFe(Image, Node);
                return;
            }

            int B = Node.IcbBlock * BlockSize;
            int Alloc = 176;
            int Length = Alloc + 8;
            WriteTag(Image, B, 261, Length, Node.IcbBlock - PartitionStart);

            int Icb = B + 16;
            WriteU32(Image, Icb + 0, 0);
            WriteU16(Image, Icb + 4, 4);
            WriteU16(Image, Icb + 6, 0);
            WriteU16(Image, Icb + 8, 1);
            Image[Icb + 10] = 0;
            Image[Icb + 11] = (byte)(Node.Vat ? 248 : FileType(Node.Type));
            WriteU32(Image, Icb + 12, 0);
            WriteU16(Image, Icb + 16, 0);
            WriteU16(Image, Icb + 18, 0);

            WriteU32(Image, B + 36, Node.User);
            WriteU32(Image, B + 40, Node.Group);
            WriteU32(Image, B + 44, Permissions(Node.Mode));
            WriteU16(Image, B + 48, IsDirectory(Node) ? (ushort)2 : (ushort)1);
            Image[B + 50] = 0;
            Image[B + 51] = 0;
            WriteU32(Image, B + 52, 0);
            WriteU64(Image, B + 56, (ulong)Node.Data.Length);
            WriteU64(Image, B + 64, (ulong)((Node.Data.Length + BlockSize - 1) / BlockSize));
            WriteTimestamp(Image, B + 72);
            WriteTimestamp(Image, B + 84);
            WriteTimestamp(Image, B + 96);
            WriteU32(Image, B + 108, 1);
            WriteLongAd(Image, B + 112, 0, 0, Vat ? 1 : 0);
            WriteRegid(Image, B + 128, "*NyaFS");
            WriteU64(Image, B + 160, (ulong)(Node.IcbBlock - PartitionStart));
            WriteU32(Image, B + 168, 0);
            WriteU32(Image, B + 172, 8);

            if (Node.Data.Length > 0)
            {
                WriteU32(Image, B + Alloc, (uint)Node.Data.Length);
                WriteU32(Image, B + Alloc + 4, (uint)(Node.DataBlock - PartitionStart));
            }
            WriteTag(Image, B, 261, Length, Node.IcbBlock - PartitionStart);
        }

        private void WriteVatFe(byte[] Image, Node Node)
        {
            int B = Node.IcbBlock * BlockSize;
            int Alloc = 216;
            int Length = Alloc + 8;
            WriteTag(Image, B, 266, Length, Node.IcbBlock - PartitionStart);

            int Icb = B + 16;
            WriteU32(Image, Icb + 0, 0);
            WriteU16(Image, Icb + 4, 4);
            WriteU16(Image, Icb + 6, 0);
            WriteU16(Image, Icb + 8, 1);
            Image[Icb + 10] = 0;
            Image[Icb + 11] = 248;
            WriteU32(Image, Icb + 12, 0);
            WriteU16(Image, Icb + 16, 0);
            WriteU16(Image, Icb + 18, 0);

            WriteU32(Image, B + 36, Node.User);
            WriteU32(Image, B + 40, Node.Group);
            WriteU32(Image, B + 44, Permissions(Node.Mode));
            WriteU16(Image, B + 48, 1);
            Image[B + 50] = 0;
            Image[B + 51] = 0;
            WriteU32(Image, B + 52, 0);
            WriteU64(Image, B + 56, (ulong)Node.Data.Length);
            WriteU64(Image, B + 64, (ulong)Node.Data.Length);
            WriteU64(Image, B + 72, (ulong)Node.Data.Length);
            WriteTimestamp(Image, B + 80);
            WriteTimestamp(Image, B + 92);
            WriteTimestamp(Image, B + 104);
            WriteTimestamp(Image, B + 116);
            WriteU32(Image, B + 132, 1);
            WriteLongAd(Image, B + 136, 0, 0, 0);
            WriteRegid(Image, B + 152, "*NyaFS");
            WriteU64(Image, B + 184, (ulong)(Node.IcbBlock - PartitionStart));
            WriteU32(Image, B + 168, 0);
            WriteU32(Image, B + 172, 8);
            WriteU32(Image, B + 208, 0);
            WriteU32(Image, B + 212, 8);

            if (Node.Data.Length > 0)
            {
                WriteU32(Image, B + Alloc, (uint)Node.Data.Length);
                WriteU32(Image, B + Alloc + 4, (uint)(Node.DataBlock - PartitionStart));
                WriteU32(Image, B + 176, (uint)Node.Data.Length);
                WriteU32(Image, B + 180, (uint)(Node.DataBlock - PartitionStart));
            }
            WriteTag(Image, B, 266, Length, Node.IcbBlock - PartitionStart);
        }

        private void WriteTag(byte[] Image, int Offset, int Id, int Length, int Location)
        {
            WriteU16(Image, Offset, (uint)Id);
            WriteU16(Image, Offset + 2, 2);
            Image[Offset + 4] = 0;
            Image[Offset + 5] = 0;
            WriteU16(Image, Offset + 6, 0);
            WriteU16(Image, Offset + 10, (uint)Math.Max(0, Length - 16));
            WriteU32(Image, Offset + 12, (uint)Location);

            ushort Crc = Crc16(Image, Offset + 16, Math.Max(0, Length - 16));
            WriteU16(Image, Offset + 8, Crc);

            byte Sum = 0;
            for (int i = 0; i < 16; i++)
                Sum += Image[Offset + i];
            Image[Offset + 4] = Sum;
        }

        private static ushort Crc16(byte[] Data, int Offset, int Length)
        {
            ushort Crc = 0;
            for (int i = 0; i < Length; i++)
            {
                Crc ^= (ushort)(Data[Offset + i] << 8);
                for (int Bit = 0; Bit < 8; Bit++)
                    Crc = (ushort)((Crc & 0x8000) != 0 ? (Crc << 1) ^ 0x1021 : Crc << 1);
            }
            return Crc;
        }

        private void WriteLongAd(byte[] Image, int Offset, uint Length, int Location, int Partition)
        {
            WriteU32(Image, Offset, Length);
            WriteU32(Image, Offset + 4, (uint)Location);
            WriteU16(Image, Offset + 8, (uint)Partition);
        }

        private void WriteTimestamp(byte[] Image, int Offset)
        {
            var Now = DateTime.UtcNow;
            Image[Offset] = 1;
            Image[Offset + 1] = 0;
            WriteU16(Image, Offset + 2, (uint)Now.Year);
            Image[Offset + 4] = (byte)Now.Month;
            Image[Offset + 5] = (byte)Now.Day;
            Image[Offset + 6] = (byte)Now.Hour;
            Image[Offset + 7] = (byte)Now.Minute;
            Image[Offset + 8] = (byte)Now.Second;
        }

        private void WriteDstring(byte[] Image, int Offset, int Size, string Value)
        {
            var Bytes = Encoding.ASCII.GetBytes(Value ?? "");
            if (Bytes.Length + 1 > Size)
                Array.Resize(ref Bytes, Size - 1);

            Image[Offset] = 8;
            Array.Copy(Bytes, 0, Image, Offset + 1, Bytes.Length);
        }

        private void WriteRegid(byte[] Image, int Offset, string Identifier)
        {
            var Bytes = Encoding.ASCII.GetBytes(Identifier ?? "");
            int Length = Math.Min(Bytes.Length, 23);
            Array.Copy(Bytes, 0, Image, Offset + 1, Length);
        }

        private static uint Permissions(uint Mode)
        {
            uint Result = 0;
            if ((Mode & 0x004) != 0) Result |= 0x0004;
            if ((Mode & 0x002) != 0) Result |= 0x0002;
            if ((Mode & 0x001) != 0) Result |= 0x0001;
            if ((Mode & 0x020) != 0) Result |= 0x0080;
            if ((Mode & 0x010) != 0) Result |= 0x0040;
            if ((Mode & 0x008) != 0) Result |= 0x0020;
            if ((Mode & 0x100) != 0) Result |= 0x1000;
            if ((Mode & 0x080) != 0) Result |= 0x0800;
            if ((Mode & 0x040) != 0) Result |= 0x0400;
            return Result;
        }

        private static int FileType(FilesystemItemType Type)
        {
            switch (Type)
            {
                case FilesystemItemType.Directory: return 4;
                case FilesystemItemType.File: return 5;
                case FilesystemItemType.Block: return 6;
                case FilesystemItemType.Character: return 7;
                case FilesystemItemType.Fifo: return 9;
                case FilesystemItemType.Socket: return 10;
                case FilesystemItemType.SymLink: return 12;
                default: return 5;
            }
        }

        private static bool IsDirectory(Node Node) => Node.Type == FilesystemItemType.Directory;

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

        private static int Align(int Value, int Alignment) =>
            (Value + Alignment - 1) / Alignment * Alignment;

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
