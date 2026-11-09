using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Udf
{
    internal sealed class UdfReader : IFilesystemReader, IRootFsInfo
    {
        private sealed class Extent
        {
            public uint Length;
            public uint Position;
            public ushort PartitionRef;
        }

        private sealed class Inode
        {
            public int Tag;
            public int FileType;
            public uint User;
            public uint Group;
            public uint Permissions;
            public ulong Size;
            public DateTime Modified;
            public int Flags;
            public int AllocOffset;
            public int AllocLength;
            public int LengthExtendedAttr;
            public ushort PartitionRef;
            public readonly List<Extent> Extents = new List<Extent>();
            public byte[] InlineData;
        }

        private sealed class Node
        {
            public string Name;
            public string Path;
            public Inode Inode;
            public Node Parent;
            public readonly List<Node> Children = new List<Node>();
        }

        private sealed class PartitionMapInfo
        {
            public int Type;
            public ushort PartitionNumber;
            public bool Virtual;
            public bool Sparable;
        }

        private readonly byte[] Data;
        private readonly int BlockSize;
        private readonly uint PartitionStart;
        private readonly Dictionary<ushort, PartitionMapInfo> PartitionMaps = new Dictionary<ushort, PartitionMapInfo>();
        private readonly Dictionary<ushort, int> PartitionStarts = new Dictionary<ushort, int>();
        private readonly Dictionary<uint, uint> VatMap = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> SparingMap = new Dictionary<uint, uint>();
        private uint SparingPacketLength = 32;
        private readonly Dictionary<string, Node> Nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly Node Root;

        public long VolumeBytes => Data.Length;

        public UdfReader(byte[] Data)
        {
            this.Data = Data ?? throw new ArgumentNullException(nameof(Data));
            BlockSize = DetectBlockSize();

            if (!IsUdf(Data, BlockSize))
                throw new InvalidOperationException("Not a UDF image");

            int Anchor = 256 * BlockSize;
            if (Anchor + 24 > Data.Length)
                throw new InvalidOperationException("UDF: anchor descriptor is out of image");

            int Tag = (int)ReadU16(Data, Anchor);
            if (Tag != 2)
                throw new InvalidOperationException("UDF: anchor descriptor not found");

            uint MainLocation = ReadU32(Data, Anchor + 20);
            uint MainLength = ReadU32(Data, Anchor + 16);
            int LvdOffset = -1;
            int PdOffset = -1;

            for (int i = 0; i < MainLength / BlockSize; i++)
            {
                int Offset = (int)(MainLocation * BlockSize + i * BlockSize);
                if (Offset + 16 > Data.Length)
                    break;

                int Descriptor = (int)ReadU16(Data, Offset);
                if (Descriptor == 6)
                    LvdOffset = Offset;
                else if (Descriptor == 5)
                {
                    PdOffset = Offset;
                    int PartitionNumber = (int)ReadU16(Data, Offset + 22);
                    PartitionStarts[(ushort)PartitionNumber] = (int)ReadU32(Data, Offset + 188);
                }
            }

            if ((LvdOffset < 0) || (PdOffset < 0))
                throw new InvalidOperationException("UDF: logical/partition descriptor not found");

            PartitionStart = ReadU32(Data, PdOffset + 188);
            uint LogicalBlockSize = ReadU32(Data, LvdOffset + 212);
            if (LogicalBlockSize != 0)
                BlockSize = (int)LogicalBlockSize;

            ReadPartitionMaps(LvdOffset, PdOffset);
            ushort FsdPartitionRef = (ushort)ReadU16(Data, LvdOffset + 256);
            uint FsdLocation = ReadU32(Data, LvdOffset + 252);
            int FsdOffset = ResolveBlock(FsdPartitionRef, FsdLocation);
            if (FsdOffset + 416 > Data.Length)
                throw new InvalidOperationException("UDF: file set descriptor is out of image");

            ushort RootPartitionRef = (ushort)ReadU16(Data, FsdOffset + 408);
            uint RootLocation = ReadU32(Data, FsdOffset + 404);
            int RootOffset = ResolveBlock(RootPartitionRef, RootLocation);
            Root = new Node
            {
                Name = "",
                Path = "",
                Inode = ReadInodeAt(RootOffset, RootPartitionRef)
            };
            Nodes[""] = Root;
            ReadDirectory(Root, new HashSet<long>());
        }

        public byte[] Read(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || IsDirectory(Node.Inode))
                return null;

            return ReadData(Node.Inode);
        }

        public string ReadLink(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Inode.FileType != 12))
                return null;

            var Raw = ReadData(Node.Inode);
            var Result = new StringBuilder();
            int Offset = 0;

            while (Offset + 3 <= Raw.Length)
            {
                int Length = Raw[Offset + 1];
                Offset += 4;
                if ((Length == 0) || (Offset + Length > Raw.Length))
                    break;

                var Component = DecodeName(Raw, Offset, Length);
                if (Result.Length > 0)
                    Result.Append("/");
                Result.Append(Component);
                Offset += Length;
            }

            return Result.ToString();
        }

        public DeviceInfo ReadDevice(string Path) => null;

        public FilesystemEntry[] ReadDir(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || !IsDirectory(Node.Inode))
                return Array.Empty<FilesystemEntry>();

            var Result = new List<FilesystemEntry>();
            foreach (var Child in Node.Children)
            {
                Result.Add(new FilesystemEntry(
                    TypeOf(Child.Inode.FileType),
                    ExFatPath.Combine(ExFatPath.Normalize(Path), Child.Name),
                    Child.Inode.User,
                    Child.Inode.Group,
                    PermissionsOf(Child.Inode.Permissions),
                    IsDirectory(Child.Inode) ? 0u : (uint)Math.Min(Child.Inode.Size, UInt32.MaxValue)));
            }
            return Result.ToArray();
        }

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            User = Root.Inode.User;
            Group = Root.Inode.Group;
            Mode = PermissionsOf(Root.Inode.Permissions);
            return true;
        }

        private void ReadPartitionMaps(int LvdOffset, int PdOffset)
        {
            int MapLength = (int)ReadU32(Data, LvdOffset + 264);
            int MapCount = (int)ReadU32(Data, LvdOffset + 268);
            int Offset = LvdOffset + 440;
            int End = Offset + MapLength;

            for (int Index = 0; (Index < MapCount) && (Offset + 2 <= End); Index++)
            {
                int Type = Data[Offset];
                int Length = Data[Offset + 1];
                if (Length < 2)
                    break;

                var Map = new PartitionMapInfo { Type = Type };
                if (Type == 1)
                {
                    Map.PartitionNumber = (ushort)ReadU16(Data, Offset + 4);
                }
                else if (Type == 2)
                {
                    string Ident = ReadRegid(Data, Offset + 4);
                    Map.PartitionNumber = (ushort)ReadU16(Data, Offset + 38);
                    Map.Virtual = Ident.StartsWith("*UDF Virtual", StringComparison.Ordinal);
                    Map.Sparable = Ident.StartsWith("*UDF Sparable", StringComparison.Ordinal);
                    if (Map.Sparable)
                    {
                        SparingPacketLength = ReadU16(Data, Offset + 40);
                        if ((SparingPacketLength == 0) || ((SparingPacketLength & (SparingPacketLength - 1)) != 0))
                            SparingPacketLength = 32;
                        ReadSparingTable(Offset);
                    }
                }

                PartitionMaps[(ushort)Index] = Map;
                Offset += Length;
            }

            if (!PartitionStarts.ContainsKey(0) && (PdOffset >= 0))
                PartitionStarts[0] = (int)ReadU32(Data, PdOffset + 188);
        }

        private void ReadSparingTable(int MapOffset)
        {
            int Count = Data[MapOffset + 42];
            int Size = (int)ReadU32(Data, MapOffset + 44);
            if ((Count <= 0) || (Size < 56))
                return;

            for (int Table = 0; Table < Count && Table < 4; Table++)
            {
                uint Location = ReadU32(Data, MapOffset + 48 + Table * 4);
                int Offset = (int)(Location * BlockSize) + 56;
                int Entries = (int)ReadU16(Data, (int)(Location * BlockSize) + 48);
                for (int Entry = 0; Entry < Entries; Entry++)
                {
                    if (Offset + 8 > Data.Length)
                        break;

                    uint Original = ReadU32(Data, Offset);
                    uint Mapped = ReadU32(Data, Offset + 4);
                    if ((Original != Mapped) && (Original != 0xFFFFFFFF))
                        SparingMap[Original] = Mapped;
                    Offset += 8;
                }
            }
        }

        private int ResolveBlock(ushort PartitionRef, uint Location)
        {
            uint Mapped = Location;
            int Start = (int)PartitionStart;

            if (PartitionMaps.TryGetValue(PartitionRef, out PartitionMapInfo Map))
            {
                if (Map.Virtual && VatMap.TryGetValue(Location, out uint Vat))
                    Mapped = Vat;
                else if (Map.Sparable)
                {
                    uint Packet = Location & ~(SparingPacketLength - 1);
                    if (SparingMap.TryGetValue(Packet, out uint Sparing))
                        return (int)((Sparing + (Location & (SparingPacketLength - 1))) * BlockSize);
                }

                if (PartitionStarts.TryGetValue(Map.PartitionNumber, out int StartByMap))
                    Start = StartByMap;
            }
            else if (PartitionStarts.TryGetValue(PartitionRef, out int StartByRef))
            {
                Start = StartByRef;
            }

            if (SparingMap.TryGetValue(Mapped, out uint GlobalSparing))
                Mapped = GlobalSparing;

            return (int)((Start + Mapped) * BlockSize);
        }

        private Node Find(string Path)
        {
            var Normalized = ExFatPath.Normalize(Path);
            return Nodes.TryGetValue(Normalized, out Node Result) ? Result : null;
        }

        private void ReadDirectory(Node Directory, HashSet<long> Visited)
        {
            if (!Visited.Add((long)Directory.Inode.Size << 32 | (long)Directory.Inode.Extents.Count))
                return;

            var Bytes = ReadData(Directory.Inode);
            int Offset = 0;

            while (Offset + 38 <= Bytes.Length)
            {
                int Tag = (int)ReadU16(Bytes, Offset);
                if (Tag != 257)
                    break;

                int Characteristics = Bytes[Offset + 18];
                int NameLength = Bytes[Offset + 19];
                ushort IcbPartitionRef = (ushort)ReadU16(Bytes, Offset + 28);
                uint IcbLocation = ReadU32(Bytes, Offset + 24);
                int ImpUseLength = (int)ReadU16(Bytes, Offset + 36);
                int NameOffset = Offset + 38 + ImpUseLength;

                if (NameOffset + NameLength > Bytes.Length)
                    break;

                int EntryLength = Align(38 + ImpUseLength + NameLength, 4);
                if (EntryLength <= 0)
                    break;

                if (NameLength > 0)
                {
                    string Name = DecodeName(Bytes, NameOffset, NameLength);
                    if (!String.IsNullOrEmpty(Name) && ((Characteristics & 0x08) != 0) && Name.Contains("Virtual"))
                    {
                        ReadVat(ResolveBlock(IcbPartitionRef, IcbLocation), IcbPartitionRef);
                    }
                    else if (!String.IsNullOrEmpty(Name) && ((Characteristics & 0x08) == 0))
                    {
                        int ChildOffset = ResolveBlock(IcbPartitionRef, IcbLocation);
                        var ChildInode = ReadInodeAt(ChildOffset, IcbPartitionRef);
                        var Child = new Node
                        {
                            Name = Name,
                            Path = ExFatPath.Combine(Directory.Path, Name),
                            Parent = Directory,
                            Inode = ChildInode
                        };

                        Directory.Children.Add(Child);
                        Nodes[Child.Path] = Child;

                        if (IsDirectory(ChildInode))
                            ReadDirectory(Child, Visited);
                    }
                }

                Offset += EntryLength;
            }
        }

        private void ReadVat(int Offset, ushort PartitionRef)
        {
            var Inode = ReadInodeAt(Offset, PartitionRef);
            var Bytes = ReadData(Inode);
            if (Bytes.Length < 152)
                return;

            int HeaderLength = (int)ReadU16(Bytes, 0);
            if ((HeaderLength < 152) || (HeaderLength > Bytes.Length))
                HeaderLength = 152;

            for (int Entry = HeaderLength; Entry + 4 <= Bytes.Length; Entry += 4)
            {
                uint Value = ReadU32(Bytes, Entry);
                if (Value != 0xFFFFFFFF)
                    VatMap[(uint)((Entry - HeaderLength) / 4)] = Value;
            }
        }

        private Inode ReadInodeAt(int Offset, ushort PartitionRef = 0)
        {
            if ((Offset < 0) || (Offset + 216 > Data.Length))
                throw new InvalidOperationException("UDF: inode is out of image");

            int Tag = (int)ReadU16(Data, Offset);
            if ((Tag != 261) && (Tag != 266))
                throw new InvalidOperationException($"UDF: unsupported inode tag {Tag}");

            int Icb = Offset + 16;
            var Result = new Inode
            {
                Tag = Tag,
                FileType = Data[Icb + 11],
                Flags = (int)ReadU16(Data, Icb + 18),
                User = ReadU32(Data, Offset + 36),
                Group = ReadU32(Data, Offset + 40),
                Permissions = ReadU32(Data, Offset + 44),
                PartitionRef = PartitionRef
            };

            if (Tag == 266)
            {
                Result.Size = ReadU64(Data, Offset + 64);
                Result.Modified = ReadTimestamp(Offset + 104);
                Result.LengthExtendedAttr = (int)ReadU32(Data, Offset + 208);
                Result.AllocLength = (int)ReadU32(Data, Offset + 212);
                Result.AllocOffset = Offset + 216 + Result.LengthExtendedAttr;
            }
            else
            {
                Result.Size = ReadU64(Data, Offset + 56);
                Result.Modified = ReadTimestamp(Offset + 84);
                Result.LengthExtendedAttr = (int)ReadU32(Data, Offset + 168);
                Result.AllocLength = (int)ReadU32(Data, Offset + 172);
                Result.AllocOffset = Offset + 176 + Result.LengthExtendedAttr;
            }

            ReadAllocationDescriptors(Result);
            return Result;
        }

        private void ReadAllocationDescriptors(Inode Inode)
        {
            int Type = Inode.Flags & 7;
            if (Type == 3)
            {
                int Length = (int)Math.Min(Inode.Size, (ulong)Math.Max(0, Inode.AllocLength));
                if ((Inode.AllocOffset >= 0) && (Inode.AllocOffset + Length <= Data.Length))
                {
                    Inode.InlineData = new byte[Length];
                    Array.Copy(Data, Inode.AllocOffset, Inode.InlineData, 0, Length);
                }
                return;
            }

            int EntrySize = Type == 0 ? 8 : (Type == 1 ? 16 : (Type == 2 ? 20 : 0));
            if (EntrySize == 0)
                return;

            for (int Offset = Inode.AllocOffset; Offset + EntrySize <= Inode.AllocOffset + Inode.AllocLength; Offset += EntrySize)
            {
                uint Length = ReadU32(Data, Offset) & 0x3FFFFFFF;
                uint Position;
                ushort PartitionRef;
                if (Type == 0)
                {
                    Position = ReadU32(Data, Offset + 4);
                    PartitionRef = Inode.PartitionRef;
                }
                else if (Type == 1)
                {
                    Position = ReadU32(Data, Offset + 4);
                    PartitionRef = (ushort)ReadU16(Data, Offset + 8);
                }
                else
                {
                    Position = ReadU32(Data, Offset + 12);
                    PartitionRef = (ushort)ReadU16(Data, Offset + 16);
                }

                if (Length == 0)
                    continue;

                Inode.Extents.Add(new Extent
                {
                    Length = Length,
                    Position = Position,
                    PartitionRef = PartitionRef
                });
            }
        }

        private byte[] ReadData(Inode Inode)
        {
            if (Inode.InlineData != null)
                return Inode.InlineData;

            if (Inode.Size > Int32.MaxValue)
                throw new InvalidOperationException("UDF: file is too large");

            var Result = new byte[(int)Inode.Size];
            int Output = 0;

            foreach (var Extent in Inode.Extents)
            {
                long Source = ResolveBlock(Extent.PartitionRef, Extent.Position);
                int Length = (int)Math.Min(Extent.Length, (uint)(Result.Length - Output));
                if ((Source < 0) || (Source >= Data.Length) || (Length <= 0))
                    break;

                int Available = Math.Min(Length, (int)(Data.Length - Source));
                Array.Copy(Data, Source, Result, Output, Available);
                Output += Available;
                if (Output >= Result.Length)
                    break;
            }

            return Result;
        }

        private DateTime ReadTimestamp(int Offset)
        {
            try
            {
                int Type = Data[Offset];
                int Year = (int)ReadU16(Data, Offset + 2);
                int Month = Data[Offset + 4];
                int Day = Data[Offset + 5];
                int Hour = Data[Offset + 6];
                int Minute = Data[Offset + 7];
                int Second = Data[Offset + 8];
                int Centi = Data[Offset + 9];
                return new DateTime(Year, Math.Max(1, Month), Math.Max(1, Day),
                    Hour, Minute, Second, Centi * 10, DateTimeKind.Utc);
            }
            catch
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }
        }

        private static string ReadRegid(byte[] Data, int Offset)
        {
            int Length = Math.Min((int)Data[Offset], 23);
            return Encoding.ASCII.GetString(Data, Offset + 1, Math.Max(0, Length));
        }

        private static string DecodeName(byte[] Data, int Offset, int Length)
        {
            if (Length <= 1)
                return "";

            var Raw = new byte[Length - 1];
            Array.Copy(Data, Offset + 1, Raw, 0, Raw.Length);

            switch (Data[Offset])
            {
                case 8:
                    return Encoding.UTF8.GetString(Raw);
                case 16:
                    return Encoding.BigEndianUnicode.GetString(Raw);
                default:
                    return Encoding.UTF8.GetString(Data, Offset, Length);
            }
        }

        private uint PermissionsOf(uint Permissions)
        {
            uint Result = 0;
            if ((Permissions & 0x0004) != 0) Result |= 0x004;
            if ((Permissions & 0x0002) != 0) Result |= 0x002;
            if ((Permissions & 0x0001) != 0) Result |= 0x001;
            if ((Permissions & 0x0080) != 0) Result |= 0x020;
            if ((Permissions & 0x0040) != 0) Result |= 0x010;
            if ((Permissions & 0x0020) != 0) Result |= 0x008;
            if ((Permissions & 0x1000) != 0) Result |= 0x400;
            if ((Permissions & 0x0800) != 0) Result |= 0x200;
            if ((Permissions & 0x0400) != 0) Result |= 0x100;
            return Result;
        }

        private static FilesystemItemType TypeOf(int FileType)
        {
            switch (FileType)
            {
                case 4: return FilesystemItemType.Directory;
                case 5: return FilesystemItemType.File;
                case 6: return FilesystemItemType.Block;
                case 7: return FilesystemItemType.Character;
                case 9: return FilesystemItemType.Fifo;
                case 10: return FilesystemItemType.Socket;
                case 12: return FilesystemItemType.SymLink;
                default: return FilesystemItemType.Unknown;
            }
        }

        private static bool IsDirectory(Inode Inode) => Inode.FileType == 4;

        public static bool IsUdfImage(byte[] Data)
        {
            foreach (var Size in new[] { 2048, 512, 1024, 4096, 8192, 16384 })
            {
                int Offset = 16 * Size;
                if ((Offset + 6 <= Data.Length) &&
                    (Encoding.ASCII.GetString(Data, Offset + 1, 5) == "BEA01"))
                    return true;
            }
            return false;
        }

        private int DetectBlockSize()
        {
            foreach (var Size in new[] { 2048, 512, 1024, 4096, 8192, 16384 })
            {
                int Offset = 16 * Size;
                if ((Offset + 5 <= Data.Length) &&
                    (Encoding.ASCII.GetString(Data, Offset + 1, 5) == "BEA01"))
                    return Size;
            }
            return 2048;
        }

        private static bool IsUdf(byte[] Data, int BlockSize)
        {
            int Offset = 16 * BlockSize;
            return (Offset + 5 <= Data.Length) &&
                (Encoding.ASCII.GetString(Data, Offset + 1, 5) == "BEA01");
        }

        private static int Align(int Value, int Alignment) =>
            (Value + Alignment - 1) / Alignment * Alignment;

        private static uint ReadU16(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8));

        private static uint ReadU32(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8) |
                   (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));

        private static ulong ReadU64(byte[] Data, int Offset) =>
            ReadU32(Data, Offset) | ((ulong)ReadU32(Data, Offset + 4) << 32);

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
