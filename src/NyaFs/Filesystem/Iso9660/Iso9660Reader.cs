using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Iso9660
{
    internal sealed class Iso9660Reader : IFilesystemReader, IRootFsInfo
    {
        private sealed class Extent
        {
            public uint Lba;
            public uint Size;
        }

        private sealed class Node
        {
            public string Name;
            public string Path;
            public FilesystemItemType Type;
            public uint User;
            public uint Group;
            public uint Mode;
            public DateTime Modified;
            public uint Size;
            public string LinkTarget;
            public uint Major;
            public uint Minor;
            public readonly List<Extent> Extents = new List<Extent>();
            public readonly List<Node> Children = new List<Node>();
        }

        private readonly byte[] Data;
        private readonly int SectorSize;
        private readonly Dictionary<string, Node> Nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly Node Root;

        private bool Joliet;
        private bool RockRidge;

        public Iso9660Reader(byte[] Data)
        {
            this.Data = Data ?? throw new ArgumentNullException(nameof(Data));

            var Primary = FindDescriptor(1, false);
            var Supplementary = FindDescriptor(2, true);

            if (Primary == null)
                throw new InvalidOperationException("ISO9660 primary volume descriptor not found");

            SectorSize = (int)Iso9660Shared.ReadU16LE(Primary, 128);
            if (SectorSize <= 0)
                SectorSize = Iso9660Shared.SectorSize;

            var PrimaryRoot = ReadRootRecord(Primary);
            RockRidge = HasRockRidge(PrimaryRoot);

            byte[] RootRecord;
            if (RockRidge)
            {
                RootRecord = PrimaryRoot;
                Joliet = false;
            }
            else if (Supplementary != null)
            {
                RootRecord = ReadRootRecord(Supplementary);
                Joliet = true;
            }
            else
            {
                RootRecord = PrimaryRoot;
                Joliet = false;
            }

            Root = ParseNode(RootRecord, "", true, new HashSet<uint>());
            Nodes[""] = Root;
            ParseDirectory(Root, new HashSet<uint>());
        }

        public byte[] Read(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Type == FilesystemItemType.Directory))
                return null;

            var Result = new byte[Node.Size];
            int Offset = 0;
            foreach (var Extent in Node.Extents)
            {
                int Available = (int)Math.Min((uint)(Result.Length - Offset), Extent.Size);
                if (Available <= 0)
                    break;

                long Start = (long)Extent.Lba * SectorSize;
                Array.Copy(Data, Start, Result, Offset, Available);
                Offset += Available;
            }

            return Result;
        }

        public string ReadLink(string Path)
        {
            var Node = Find(Path);
            return (Node != null && Node.Type == FilesystemItemType.SymLink) ? Node.LinkTarget : null;
        }

        public DeviceInfo ReadDevice(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Type != FilesystemItemType.Character && Node.Type != FilesystemItemType.Block))
                return null;

            return new DeviceInfo(Node.Major, Node.Minor);
        }

        public FilesystemEntry[] ReadDir(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || (Node.Type != FilesystemItemType.Directory))
                return new FilesystemEntry[0];

            var Result = new List<FilesystemEntry>();
            foreach (var Child in Node.Children)
            {
                var ChildPath = Iso9660Shared.Combine(Iso9660Shared.Normalize(Path), Child.Name);
                var Size = (Child.Type == FilesystemItemType.Directory) ? 0u : Child.Size;
                Result.Add(new FilesystemEntry(Child.Type, ChildPath, Child.User, Child.Group, Child.Mode, Size));
            }

            return Result.ToArray();
        }

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            User = Root.User;
            Group = Root.Group;
            Mode = Root.Mode;
            return true;
        }

        private Node Find(string Path)
        {
            var Normalized = Iso9660Shared.Normalize(Path);
            return Nodes.TryGetValue(Normalized, out Node Result) ? Result : null;
        }

        private byte[] FindDescriptor(byte Type, bool RequireJoliet)
        {
            for (int Sector = Iso9660Shared.PrimaryVolumeDescriptorSector; ; Sector++)
            {
                long Offset = (long)Sector * Iso9660Shared.SectorSize;
                if (Offset + Iso9660Shared.SectorSize > Data.Length)
                    return null;

                if ((Data[Offset] == 0xFF) ||
                    (Data[Offset + 1] != 'C') || (Data[Offset + 2] != 'D') ||
                    (Data[Offset + 3] != '0') || (Data[Offset + 4] != '0') ||
                    (Data[Offset + 5] != '1'))
                    return null;

                if (Data[Offset] == Type)
                {
                    var Descriptor = new byte[Iso9660Shared.SectorSize];
                    Array.Copy(Data, Offset, Descriptor, 0, Descriptor.Length);

                    if (!RequireJoliet || IsJoliet(Descriptor))
                        return Descriptor;
                }

                if (Data[Offset] == 0xFF)
                    return null;
            }
        }

        private static bool IsJoliet(byte[] Descriptor)
        {
            int Escape = 88;
            return (Descriptor[Escape] == '%') &&
                   (Descriptor[Escape + 1] == '/') &&
                   ((Descriptor[Escape + 2] == '@') ||
                    (Descriptor[Escape + 2] == 'C') ||
                    (Descriptor[Escape + 2] == 'E'));
        }

        private static byte[] ReadRootRecord(byte[] Descriptor)
        {
            int Length = Descriptor[Iso9660Shared.RootRecordOffset];
            var Result = new byte[Length];
            Array.Copy(Descriptor, Iso9660Shared.RootRecordOffset, Result, 0, Length);
            return Result;
        }

        private bool HasRockRidge(byte[] RootRecord)
        {
            var Entry = ParseRawRecord(RootRecord, 0);
            if (Entry == null)
                return false;

            long Start = (long)Entry.Lba * Iso9660Shared.SectorSize;
            if (Start >= Data.Length)
                return false;

            int Length = Data[Start];
            if (Length < Iso9660Shared.DirectoryRecordFixedSize)
                return false;

            var Record = new byte[Length];
            Array.Copy(Data, Start, Record, 0, Length);
            var First = ParseRawRecord(Record, 0);
            if (First == null)
                return false;

            return ContainsSp(GetSystemUse(Record, 0, First));
        }

        private static bool ContainsSp(byte[] SystemUse)
        {
            for (int Offset = 0; Offset + 7 <= SystemUse.Length;)
            {
                int Length = SystemUse[Offset + 2];
                if (Length < 4)
                    break;

                if ((SystemUse[Offset] == 'S') && (SystemUse[Offset + 1] == 'P') &&
                    (Length >= 7) && (SystemUse[Offset + 4] == 0xBE) && (SystemUse[Offset + 5] == 0xEF))
                    return true;

                Offset += Length;
            }

            return false;
        }

        private void ParseDirectory(Node Parent, HashSet<uint> Visited)
        {
            if (Parent.Extents.Count == 0)
                return;

            var First = Parent.Extents[0];
            if (!Visited.Add(First.Lba))
                return;

            long Start = (long)First.Lba * SectorSize;
            long End = Math.Min(Data.Length, Start + First.Size);
            int Offset = 0;

            while (Start + Offset < End)
            {
                int RecordLength = Data[Start + Offset];
                if (RecordLength == 0)
                {
                    int NextSector = ((Offset / SectorSize) + 1) * SectorSize;
                    if (NextSector <= Offset)
                        break;

                    Offset = NextSector;
                    continue;
                }

                if (Offset + RecordLength > First.Size)
                    break;

                var Raw = new byte[RecordLength];
                Array.Copy(Data, Start + Offset, Raw, 0, RecordLength);

                var Parsed = ParseRawRecord(Raw, 0);
                if (Parsed != null)
                {
                    var Name = GetRecordName(Raw, Parsed);

                    if ((Name == ".") && (Parent.Path == "") && RockRidge)
                    {
                        var RootSystemUse = GetSystemUse(Raw, 0, Parsed);
                        var IgnoredName = Name;
                        ParseRockRidge(RootSystemUse, Parent, ref IgnoredName);
                    }

                    if (!String.IsNullOrEmpty(Name) && (Name != ".") && (Name != ".."))
                    {
                        var Child = ParseNode(Raw, Parent.Path, false, Visited, Parent);
                        if (Child != null)
                        {
                            Child.Path = Iso9660Shared.Combine(Parent.Path, Child.Name);

                            var Existing = Parent.Children.Find(C => C.Name == Child.Name);
                            if ((Existing != null) && ((Parsed.Flags & 0x80) != 0))
                            {
                                Existing.Extents.AddRange(Child.Extents);
                                Existing.Size += Child.Size;
                            }
                            else
                            {
                                Parent.Children.Add(Child);
                                Nodes[Child.Path] = Child;
                            }
                        }
                    }
                }

                Offset += RecordLength;
            }

            foreach (var Child in Parent.Children.ToArray())
            {
                if (Child.Type == FilesystemItemType.Directory)
                    ParseDirectory(Child, Visited);
            }
        }

        private Node ParseNode(byte[] Record, string ParentPath, bool IsRoot, HashSet<uint> Visited, Node Parent = null)
        {
            var Parsed = ParseRawRecord(Record, 0);
            if (Parsed == null)
                return null;

            var Name = IsRoot ? "" : GetRecordName(Record, Parsed);
            if (!IsRoot && (String.IsNullOrEmpty(Name) || (Name == ".") || (Name == "..")))
                return null;

            var Node = new Node
            {
                Name = Name,
                Path = IsRoot ? "" : Name,
                Type = ((Parsed.Flags & 0x02) != 0) ? FilesystemItemType.Directory : FilesystemItemType.File,
                Modified = Iso9660Shared.ReadRecordingDate(Record, 18),
                Size = Parsed.DataLength,
                Mode = ((Parsed.Flags & 0x02) != 0) ? 0x1EDu : 0x1A4u
            };

            Node.Extents.Add(new Extent { Lba = Parsed.Lba, Size = Parsed.DataLength });

            var SystemUse = GetSystemUse(Record, 0, Parsed);
            if (RockRidge || IsRoot)
                ParseRockRidge(SystemUse, Node, ref Name);

            if (IsRoot)
                Node.Name = "";
            else
                Node.Name = Name;

            if (Node.Type == FilesystemItemType.Directory)
            {
                Node.Size = 0;
            }

            return Node;
        }

        private static RawRecord ParseRawRecord(byte[] Record, int Offset)
        {
            if ((Offset + Iso9660Shared.DirectoryRecordFixedSize > Record.Length) ||
                (Record[Offset] < Iso9660Shared.DirectoryRecordFixedSize))
                return null;

            var Result = new RawRecord
            {
                Length = Record[Offset],
                Lba = Iso9660Shared.ReadU32LE(Record, Offset + 2),
                DataLength = Iso9660Shared.ReadU32LE(Record, Offset + 10),
                Flags = Record[Offset + 25],
                NameLength = Record[Offset + 32]
            };

            return Result;
        }

        private static byte[] GetSystemUse(byte[] Record, int Offset, RawRecord Parsed)
        {
            int NameOffset = Offset + Iso9660Shared.DirectoryRecordFixedSize;
            int NameLength = Parsed.NameLength;
            int SystemUseOffset = NameOffset + NameLength + ((NameLength % 2 == 0) ? 1 : 0);
            int RecordEnd = Offset + Parsed.Length;

            if (SystemUseOffset >= RecordEnd)
                return new byte[0];

            var Result = new byte[RecordEnd - SystemUseOffset];
            Array.Copy(Record, SystemUseOffset, Result, 0, Result.Length);
            return Result;
        }

        private string GetRecordName(byte[] Record, RawRecord Parsed)
        {
            int Offset = Iso9660Shared.DirectoryRecordFixedSize;
            if (Parsed.NameLength == 1)
            {
                if (Record[Offset] == 0) return ".";
                if (Record[Offset] == 1) return "..";
            }

            if (Joliet)
                return Iso9660Shared.ReadJolietName(Record, Offset, Parsed.NameLength);

            var Name = Encoding.ASCII.GetString(Record, Offset, Parsed.NameLength);
            return Iso9660Shared.IsoNameToDisplay(Name);
        }

        private static void ParseRockRidge(byte[] SystemUse, Node Node, ref string Name)
        {
            string RrName = null;

            for (int Offset = 0; Offset + 4 <= SystemUse.Length;)
            {
                int Length = SystemUse[Offset + 2];
                if (Length < 4)
                    break;

                var Signature = Encoding.ASCII.GetString(SystemUse, Offset, 2);

                switch (Signature)
                {
                    case "NM":
                        {
                            byte Flags = SystemUse[Offset + 4];
                            var Part = Encoding.UTF8.GetString(SystemUse, Offset + 5, Length - 5);
                            if ((Flags & 0x06) == 0)
                                RrName = (RrName ?? "") + Part;
                        }
                        break;

                    case "PX":
                        {
                            if (Length >= 44)
                            {
                                uint Mode = Iso9660Shared.ReadU32LE(SystemUse, Offset + 4);
                                Node.User = Iso9660Shared.ReadU32LE(SystemUse, Offset + 20);
                                Node.Group = Iso9660Shared.ReadU32LE(SystemUse, Offset + 28);
                                Node.Mode = Mode & 0xFFF;

                                switch (Mode & 0xF000)
                                {
                                    case 0x4000: Node.Type = FilesystemItemType.Directory; break;
                                    case 0xA000: Node.Type = FilesystemItemType.SymLink; break;
                                    case 0x2000: Node.Type = FilesystemItemType.Character; break;
                                    case 0x6000: Node.Type = FilesystemItemType.Block; break;
                                    case 0x1000: Node.Type = FilesystemItemType.Fifo; break;
                                    case 0xC000: Node.Type = FilesystemItemType.Socket; break;
                                    case 0x8000: Node.Type = FilesystemItemType.File; break;
                                }
                            }
                        }
                        break;

                    case "SL":
                        {
                            bool Absolute = false;
                            var Parts = new List<string>();
                            int Position = Offset + 5;
                            int End = Offset + Length;
                            while (Position + 2 <= End)
                            {
                                byte ComponentFlags = SystemUse[Position];
                                int ComponentLength = SystemUse[Position + 1];
                                Position += 2;

                                if (Position + ComponentLength > End)
                                    break;

                                if ((ComponentFlags & 0x08) != 0)
                                    Absolute = true;
                                else if ((ComponentFlags & 0x02) != 0)
                                    Parts.Add(".");
                                else if ((ComponentFlags & 0x04) != 0)
                                    Parts.Add("..");
                                else
                                    Parts.Add(Encoding.UTF8.GetString(SystemUse, Position, ComponentLength));

                                Position += ComponentLength;
                            }

                            Node.LinkTarget = (Absolute ? "/" : "") + String.Join("/", Parts);
                            Node.Type = FilesystemItemType.SymLink;
                        }
                        break;

                    case "PN":
                        if (Length >= 12)
                        {
                            Node.Major = Iso9660Shared.ReadU32LE(SystemUse, Offset + 4);
                            Node.Minor = Iso9660Shared.ReadU32LE(SystemUse, Offset + 8);
                        }
                        break;

                    case "TF":
                        {
                            if (Length >= 5)
                            {
                                byte Flags = SystemUse[Offset + 4];
                                int Position = Offset + 5;
                                if ((Flags & 0x02) != 0 && Position + 7 <= Offset + Length)
                                    Node.Modified = Iso9660Shared.ReadRecordingDate(SystemUse, Position);
                            }
                        }
                        break;
                }

                Offset += Length;
            }

            if (!String.IsNullOrEmpty(RrName) && (RrName != ".") && (RrName != ".."))
                Name = RrName;
        }

        private sealed class RawRecord
        {
            public int Length;
            public uint Lba;
            public uint DataLength;
            public byte Flags;
            public int NameLength;
        }
    }
}
