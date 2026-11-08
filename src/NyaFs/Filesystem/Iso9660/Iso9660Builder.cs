using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Iso9660
{
    public sealed class Iso9660Builder : IFilesystemBuilder
    {
        private sealed class Node
        {
            public string Path;
            public string Name;
            public Node Parent;
            public FilesystemItemType Type;
            public uint User;
            public uint Group;
            public uint Mode;
            public byte[] Data;
            public string LinkTarget;
            public uint Major;
            public uint Minor;
            public readonly List<Node> Children = new List<Node>();

            public string IsoName;
            public uint Extent;
            public uint DataLength;
            public uint DirectorySize;

            public bool IsDirectory => Type == FilesystemItemType.Directory;
        }

        private sealed class DirectoryRecord
        {
            public Node Node;
            public byte[] Identifier;
            public bool Dot;
            public bool DotDot;
        }

        private readonly List<Node> Directories = new List<Node>();
        private Node Root;

        public void Directory(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.Directory, null, null);

        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.File, Content ?? Array.Empty<byte>(), null);

        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.SymLink, Encoding.UTF8.GetBytes(Target ?? ""), Target);

        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.Character, null, null, Major, Minor);

        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.Block, null, null, Major, Minor);

        public void Fifo(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.Fifo, null, null);

        public void Socket(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, FilesystemItemType.Socket, null, null);

        public byte[] GetFilesystemImage()
        {
            EnsureRoot();
            AssignIsoNames(Root);

            CollectDirectories(Root);

            foreach (var Dir in Directories)
                Dir.DirectorySize = MeasureDirectory(Dir);

            int PathTableSize = MeasurePathTables();
            int PathTableSectors = (PathTableSize + Iso9660Shared.SectorSize - 1) / Iso9660Shared.SectorSize;
            int LPathTableSector = Iso9660Shared.PrimaryVolumeDescriptorSector + 2;
            int MPathTableSector = LPathTableSector + PathTableSectors;
            int CurrentSector = MPathTableSector + PathTableSectors;

            foreach (var Dir in Directories)
            {
                Dir.Extent = (uint)CurrentSector;
                CurrentSector += (int)((Dir.DirectorySize + Iso9660Shared.SectorSize - 1) / Iso9660Shared.SectorSize);
            }

            AssignFileExtents(Root, ref CurrentSector);

            int VolumeSectors = CurrentSector;
            var Image = new byte[(long)VolumeSectors * Iso9660Shared.SectorSize];

            WritePathTables(Image, LPathTableSector, MPathTableSector, PathTableSectors);

            foreach (var Dir in Directories)
                WriteDirectory(Image, Dir);

            WriteFiles(Image, Root);
            WriteDescriptors(Image, VolumeSectors, PathTableSize, LPathTableSector, MPathTableSector);

            return Image;
        }

        private void EnsureRoot()
        {
            if (Root != null)
                return;

            Root = new Node
            {
                Path = "",
                Name = "",
                Type = FilesystemItemType.Directory,
                Mode = 0x1ED
            };
        }

        private void GetOrCreate(string Path, uint User, uint Group, uint Mode, FilesystemItemType Type,
            byte[] Data, string LinkTarget, uint Major = 0, uint Minor = 0)
        {
            EnsureRoot();
            var Normalized = Iso9660Shared.Normalize(Path);
            if (Normalized.Length == 0)
            {
                Root.User = User;
                Root.Group = Group;
                Root.Mode = Mode & 0xFFF;
                return;
            }

            var Parts = Normalized.Split('/');
            var Current = Root;
            for (int i = 0; i < Parts.Length - 1; i++)
            {
                var Child = Current.Children.Find(C => C.Name == Parts[i] && C.IsDirectory);
                if (Child == null)
                {
                    Child = new Node
                    {
                        Name = Parts[i],
                        Path = Iso9660Shared.Combine(Current.Path, Parts[i]),
                        Parent = Current,
                        Type = FilesystemItemType.Directory,
                        Mode = 0x1ED
                    };
                    Current.Children.Add(Child);
                }

                Current = Child;
            }

            var Name = Parts[Parts.Length - 1];
            var Existing = Current.Children.Find(C => C.Name == Name);
            if (Existing == null)
            {
                Existing = new Node
                {
                    Name = Name,
                    Path = Iso9660Shared.Combine(Current.Path, Name),
                    Parent = Current
                };
                Current.Children.Add(Existing);
            }

            Existing.Type = Type;
            Existing.User = User;
            Existing.Group = Group;
            Existing.Mode = Mode & 0xFFF;
            Existing.Data = Data;
            Existing.LinkTarget = LinkTarget;
            Existing.Major = Major;
            Existing.Minor = Minor;
            Existing.DataLength = (uint)(Data?.Length ?? 0);
        }

        private static void AssignIsoNames(Node Directory)
        {
            var Used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var Child in Directory.Children)
            {
                Child.IsoName = MakeIsoName(Child, Used);
                if (Child.IsDirectory)
                    AssignIsoNames(Child);
            }

            Directory.Children.Sort((A, B) => StringComparer.Ordinal.Compare(A.IsoName, B.IsoName));
        }

        private static string MakeIsoName(Node Node, HashSet<string> Used)
        {
            var Original = Node.Name;
            var Base = Original;
            var Extension = "";

            if (!Node.IsDirectory)
            {
                int Dot = Original.LastIndexOf('.');
                if (Dot > 0)
                {
                    Base = Original.Substring(0, Dot);
                    Extension = Original.Substring(Dot + 1);
                }
            }

            Base = Sanitize(Base, 8);
            Extension = Sanitize(Extension, 3);

            if (Base.Length == 0)
                Base = Node.IsDirectory ? "DIR" : "FILE";

            var Candidate = Base + (Extension.Length > 0 ? "." + Extension : "");
            if (!Node.IsDirectory)
                Candidate += ";1";

            int Suffix = 1;
            while (!Used.Add(Candidate))
            {
                var SuffixText = "~" + Suffix++;
                var MaxBase = 8 - (SuffixText.Length + (Extension.Length > 0 ? Extension.Length + 1 : 0));
                if (MaxBase < 1)
                    MaxBase = 1;

                var Name = Base.Substring(0, Math.Min(Base.Length, MaxBase)) + SuffixText;
                Candidate = Name + (Extension.Length > 0 ? "." + Extension : "") + (Node.IsDirectory ? "" : ";1");
            }

            return Candidate;
        }

        private static string Sanitize(string Value, int MaxLength)
        {
            var Builder = new StringBuilder();
            foreach (var C in (Value ?? "").ToUpperInvariant())
            {
                if ((C >= 'A' && C <= 'Z') || (C >= '0' && C <= '9') || (C == '_') || (C == '-'))
                    Builder.Append(C);
                else
                    Builder.Append('_');

                if (Builder.Length >= MaxLength)
                    break;
            }

            return Builder.ToString().TrimEnd('_');
        }

        private void CollectDirectories(Node Directory)
        {
            Directories.Add(Directory);
            foreach (var Child in Directory.Children)
            {
                if (Child.IsDirectory)
                    CollectDirectories(Child);
            }
        }

        private int MeasurePathTables()
        {
            int Total = 0;
            foreach (var Dir in Directories)
            {
                int NameLength = (Dir.Parent == null) ? 1 : Encoding.ASCII.GetByteCount(Dir.IsoName);
                Total += 8 + NameLength + ((NameLength & 1) != 0 ? 1 : 0);
            }

            return Total;
        }

        private uint MeasureDirectory(Node Directory)
        {
            int Offset = 0;
            foreach (var Record in BuildRecordList(Directory))
            {
                int Length = BuildRecord(Record, Directory, 0, 0, true).Length;
                if ((Offset % Iso9660Shared.SectorSize) + Length > Iso9660Shared.SectorSize)
                    Offset = ((Offset / Iso9660Shared.SectorSize) + 1) * Iso9660Shared.SectorSize;

                Offset += Length;
            }

            return (uint)(((Offset + Iso9660Shared.SectorSize - 1) / Iso9660Shared.SectorSize) * Iso9660Shared.SectorSize);
        }

        private List<DirectoryRecord> BuildRecordList(Node Directory)
        {
            var Result = new List<DirectoryRecord>
            {
                new DirectoryRecord { Node = Directory, Identifier = new byte[] { 0 }, Dot = true },
                new DirectoryRecord { Node = Directory.Parent ?? Directory, Identifier = new byte[] { 1 }, DotDot = true }
            };

            foreach (var Child in Directory.Children)
            {
                Result.Add(new DirectoryRecord
                {
                    Node = Child,
                    Identifier = Encoding.ASCII.GetBytes(Child.IsoName)
                });
            }

            return Result;
        }

        private void WriteDirectory(byte[] Image, Node Directory)
        {
            long Base = (long)Directory.Extent * Iso9660Shared.SectorSize;
            int Offset = 0;

            foreach (var Record in BuildRecordList(Directory))
            {
                var Extent = Record.Dot ? Directory.Extent :
                    (Record.DotDot ? (Directory.Parent ?? Directory).Extent : Record.Node.Extent);
                var DataLength = Record.Dot ? Directory.DirectorySize :
                    (Record.DotDot ? (Directory.Parent ?? Directory).DirectorySize :
                    (Record.Node.IsDirectory ? Record.Node.DirectorySize : Record.Node.DataLength));

                var Data = BuildRecord(Record, Directory, Extent, DataLength, true);

                if ((Offset % Iso9660Shared.SectorSize) + Data.Length > Iso9660Shared.SectorSize)
                    Offset = ((Offset / Iso9660Shared.SectorSize) + 1) * Iso9660Shared.SectorSize;

                Array.Copy(Data, 0, Image, Base + Offset, Data.Length);
                Offset += Data.Length;
            }
        }

        private byte[] BuildRecord(DirectoryRecord Record, Node Directory, uint Extent, uint DataLength, bool WithSystemUse)
        {
            var Name = Record.Identifier;
            int NamePadding = (Name.Length % 2 == 0) ? 1 : 0;
            var SystemUse = WithSystemUse ? BuildSystemUse(Record, Directory) : Array.Empty<byte>();

            int Total = Iso9660Shared.DirectoryRecordFixedSize + Name.Length + NamePadding + SystemUse.Length;
            if ((Total & 1) != 0)
                Total++;

            var Result = new byte[Total];
            Result[0] = (byte)Total;
            Result[1] = 0;
            Iso9660Shared.WriteBoth32(Result, 2, Extent);
            Iso9660Shared.WriteBoth32(Result, 10, DataLength);
            Iso9660Shared.WriteRecordingDate(Result, 18, DateTime.UtcNow);
            Result[25] = (byte)(Record.Node.IsDirectory ? 0x02 : 0x00);
            Result[26] = 0;
            Result[27] = 0;
            Iso9660Shared.WriteBoth16(Result, 28, 1);
            Result[32] = (byte)Name.Length;
            Array.Copy(Name, 0, Result, 33, Name.Length);

            int Position = 33 + Name.Length + NamePadding;
            if (WithSystemUse)
            {
                Array.Copy(SystemUse, 0, Result, Position, SystemUse.Length);
                Position += SystemUse.Length;
            }

            return Result;
        }

        private byte[] BuildSystemUse(DirectoryRecord Record, Node Directory)
        {
            var Result = new List<byte>();
            var Node = Record.Node;

            if (Record.Dot && (Directory.Parent == null))
                Result.AddRange(new byte[] { (byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0 });

            Result.AddRange(BuildPx(Node, Directory));

            if (!Record.Dot && !Record.DotDot)
                AddName(Result, Node.Name);

            if (!Record.Dot && !Record.DotDot && Node.Type == FilesystemItemType.SymLink)
                AddSl(Result, Node.LinkTarget);

            if (!Record.Dot && !Record.DotDot &&
                (Node.Type == FilesystemItemType.Character || Node.Type == FilesystemItemType.Block))
                AddPn(Result, Node.Major, Node.Minor);

            return Result.ToArray();
        }

        private byte[] BuildPx(Node Node, Node Directory)
        {
            var Result = new byte[44];
            Result[0] = (byte)'P';
            Result[1] = (byte)'X';
            Result[2] = 44;
            Result[3] = 1;

            uint Mode = Node.Type switch
            {
                FilesystemItemType.Directory => 0x4000u,
                FilesystemItemType.SymLink => 0xA000u,
                FilesystemItemType.Character => 0x2000u,
                FilesystemItemType.Block => 0x6000u,
                FilesystemItemType.Fifo => 0x1000u,
                FilesystemItemType.Socket => 0xC000u,
                _ => 0x8000u
            };

            Mode |= Node.Mode & 0xFFF;

            uint Links = 1;
            if (Node.IsDirectory)
            {
                Links = 2;
                foreach (var Child in Node.Children)
                    if (Child.IsDirectory)
                        Links++;
            }

            Iso9660Shared.WriteBoth32(Result, 4, Mode);
            Iso9660Shared.WriteBoth32(Result, 12, Links);
            Iso9660Shared.WriteBoth32(Result, 20, Node.User);
            Iso9660Shared.WriteBoth32(Result, 28, Node.Group);
            Iso9660Shared.WriteBoth32(Result, 36, 0);
            return Result;
        }

        private static void AddName(List<byte> Result, string Name)
        {
            var Bytes = Encoding.UTF8.GetBytes(Name ?? "");
            if (Bytes.Length > 150)
                Array.Resize(ref Bytes, 150);

            Result.Add((byte)'N');
            Result.Add((byte)'M');
            Result.Add((byte)(5 + Bytes.Length));
            Result.Add(1);
            Result.Add(0);
            Result.AddRange(Bytes);
        }

        private static void AddSl(List<byte> Result, string Target)
        {
            var Components = new List<byte>();
            bool Absolute = (Target ?? "").StartsWith("/", StringComparison.Ordinal);

            foreach (var Part in (Target ?? "").Trim('/').Split('/'))
            {
                if (Part.Length == 0)
                    continue;

                byte Flags = 0;
                if (Part == ".")
                    Flags = 0x02;
                else if (Part == "..")
                    Flags = 0x04;

                var Bytes = Encoding.UTF8.GetBytes(Part);
                Components.Add(Flags);
                Components.Add((byte)Bytes.Length);
                Components.AddRange(Bytes);
            }

            if (Absolute)
            {
                Components.Insert(0, 0);
                Components.Insert(0, 0x08);
            }

            Result.Add((byte)'S');
            Result.Add((byte)'L');
            Result.Add((byte)(5 + Components.Count));
            Result.Add(1);
            Result.Add(0);
            Result.AddRange(Components);
        }

        private static void AddPn(List<byte> Result, uint Major, uint Minor)
        {
            Result.Add((byte)'P');
            Result.Add((byte)'N');
            Result.Add(20);
            Result.Add(1);

            var Bytes = new byte[16];
            Iso9660Shared.WriteBoth32(Bytes, 0, Major);
            Iso9660Shared.WriteBoth32(Bytes, 8, Minor);
            Result.AddRange(Bytes);
        }

        private void AssignFileExtents(Node Directory, ref int CurrentSector)
        {
            foreach (var Child in Directory.Children)
            {
                if (Child.IsDirectory)
                {
                    AssignFileExtents(Child, ref CurrentSector);
                    continue;
                }

                Child.Extent = (uint)CurrentSector;
                if ((Child.DataLength > 0) && (Child.Data != null))
                    CurrentSector += (int)((Child.DataLength + Iso9660Shared.SectorSize - 1) / Iso9660Shared.SectorSize);
            }
        }

        private void WriteFiles(byte[] Image, Node Directory)
        {
            foreach (var Child in Directory.Children)
            {
                if (Child.IsDirectory)
                {
                    WriteFiles(Image, Child);
                    continue;
                }

                if ((Child.Data != null) && (Child.Data.Length > 0))
                    Array.Copy(Child.Data, 0, Image, (long)Child.Extent * Iso9660Shared.SectorSize, Child.Data.Length);
            }
        }

        private void WritePathTables(byte[] Image, int LPathTableSector, int MPathTableSector, int PathTableSectors)
        {
            var L = new List<byte>();
            var M = new List<byte>();

            for (int i = 0; i < Directories.Count; i++)
            {
                var Dir = Directories[i];
                var Name = (Dir.Parent == null) ? new byte[] { 0 } : Encoding.ASCII.GetBytes(Dir.IsoName);
                int Length = 8 + Name.Length + ((Name.Length & 1) != 0 ? 1 : 0);
                int Parent = (Dir.Parent == null) ? 1 : Directories.IndexOf(Dir.Parent) + 1;

                var LRecord = new byte[Length];
                LRecord[0] = (byte)Name.Length;
                LRecord[1] = 0;
                Iso9660Shared.WriteU32LE(LRecord, 2, Dir.Extent);
                Iso9660Shared.WriteU16LE(LRecord, 6, (uint)Parent);
                Array.Copy(Name, 0, LRecord, 8, Name.Length);
                L.AddRange(LRecord);

                var MRecord = new byte[Length];
                MRecord[0] = (byte)Name.Length;
                MRecord[1] = 0;
                Iso9660Shared.WriteU32BE(MRecord, 2, Dir.Extent);
                Iso9660Shared.WriteU16BE(MRecord, 6, (uint)Parent);
                Array.Copy(Name, 0, MRecord, 8, Name.Length);
                M.AddRange(MRecord);
            }

            Array.Copy(L.ToArray(), 0, Image, (long)LPathTableSector * Iso9660Shared.SectorSize, L.Count);
            Array.Copy(M.ToArray(), 0, Image, (long)MPathTableSector * Iso9660Shared.SectorSize, M.Count);
        }

        private void WriteDescriptors(byte[] Image, int VolumeSectors, int PathTableSize, int LPathTableSector, int MPathTableSector)
        {
            long Pvd = (long)Iso9660Shared.PrimaryVolumeDescriptorSector * Iso9660Shared.SectorSize;
            var Descriptor = new byte[Iso9660Shared.SectorSize];
            Descriptor[0] = 1;
            Descriptor[1] = (byte)'C';
            Descriptor[2] = (byte)'D';
            Descriptor[3] = (byte)'0';
            Descriptor[4] = (byte)'0';
            Descriptor[5] = (byte)'1';
            Descriptor[6] = 1;
            Iso9660Shared.WriteAscii(Descriptor, 8, 32, "NYAFS");
            Iso9660Shared.WriteAscii(Descriptor, 40, 32, "NYAFS");
            Iso9660Shared.WriteBoth32(Descriptor, 80, (uint)VolumeSectors);
            Iso9660Shared.WriteBoth16(Descriptor, 120, 1);
            Iso9660Shared.WriteBoth16(Descriptor, 124, 1);
            Iso9660Shared.WriteBoth16(Descriptor, 128, Iso9660Shared.SectorSize);
            Iso9660Shared.WriteBoth32(Descriptor, 132, (uint)PathTableSize);
            Iso9660Shared.WriteU32LE(Descriptor, 140, (uint)LPathTableSector);
            Iso9660Shared.WriteU32BE(Descriptor, 148, (uint)MPathTableSector);
            Iso9660Shared.WriteAscii(Descriptor, 190, 128, "NYAFS");
            Iso9660Shared.WriteAscii(Descriptor, 318, 128, "NYAFS");
            Iso9660Shared.WriteAscii(Descriptor, 446, 128, "NYAFS");
            Iso9660Shared.WriteAscii(Descriptor, 574, 128, "NYAFS");
            Iso9660Shared.WriteAscii(Descriptor, 702, 37, "");
            Iso9660Shared.WriteAscii(Descriptor, 739, 37, "");
            Iso9660Shared.WriteAscii(Descriptor, 776, 37, "");
            WritePvdDate(Descriptor, 813, DateTime.UtcNow);
            WritePvdDate(Descriptor, 830, DateTime.UtcNow);
            WritePvdDate(Descriptor, 847, DateTime.UtcNow);
            WritePvdDate(Descriptor, 864, DateTime.UtcNow);
            Descriptor[881] = 1;

            var RootRecord = BuildRootRecord();
            Array.Copy(RootRecord, 0, Descriptor, Iso9660Shared.RootRecordOffset, RootRecord.Length);
            Array.Copy(Descriptor, 0, Image, Pvd, Descriptor.Length);

            long Terminator = Pvd + Iso9660Shared.SectorSize;
            Array.Clear(Image, (int)Terminator, Iso9660Shared.SectorSize);
            Image[Terminator] = 0xFF;
            Image[Terminator + 1] = (byte)'C';
            Image[Terminator + 2] = (byte)'D';
            Image[Terminator + 3] = (byte)'0';
            Image[Terminator + 4] = (byte)'0';
            Image[Terminator + 5] = (byte)'1';
            Image[Terminator + 6] = 1;
        }

        private static void WritePvdDate(byte[] Data, int Offset, DateTime Time)
        {
            var Utc = Time.ToUniversalTime();
            var Text = Utc.ToString("yyyyMMddHHmmss") + "00";
            Iso9660Shared.WriteAscii(Data, Offset, 16, Text);
            Data[Offset + 16] = 0;
        }

        private byte[] BuildRootRecord()
        {
            var Record = new byte[34];
            Record[0] = 34;
            Record[1] = 0;
            Iso9660Shared.WriteBoth32(Record, 2, Root.Extent);
            Iso9660Shared.WriteBoth32(Record, 10, Root.DirectorySize);
            Iso9660Shared.WriteRecordingDate(Record, 18, DateTime.UtcNow);
            Record[25] = 0x02;
            Iso9660Shared.WriteBoth16(Record, 28, 1);
            Record[32] = 1;
            Record[33] = 0;
            return Record;
        }
    }
}
