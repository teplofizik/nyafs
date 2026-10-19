using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Items;
using NyaFs.Filesystem.Universal.Types;
using NyaFs.ImageFormat.Elements.Fs;
using CChar = NyaFs.Filesystem.Universal.Items.Char;

namespace NyaFs.Packages
{
    /// <summary>
    /// One package = one file: gzip(tar) with a "PACKAGE" control file and a "data/" tree.
    /// </summary>
    public class NyaPackage
    {
        public class Entry
        {
            public string Path = "";
            public FilesystemItemType Type = FilesystemItemType.File;
            public bool IsDir;
            public bool IsLink;
            public string Target;
            public byte[] Content;
            public uint User;
            public uint Group;
            public uint Mode;
            public uint Major;
            public uint Minor;
            public Dictionary<string, byte[]> Xattrs;
        }

        public PackageMetadata Metadata = new PackageMetadata();
        public readonly List<Entry> Entries = new List<Entry>();

        private const string ControlName = "PACKAGE";
        private const string DataDir = "data";

        // ---- create ----

        public static NyaPackage FromFilesystem(LinuxFilesystem Fs, string Subtree, PackageMetadata Meta)
        {
            var Res = new NyaPackage { Metadata = Meta ?? new PackageMetadata() };

            var Base = Normalize(Subtree);
            var Dir = (Base.Length == 0) ? Fs.Fs.Root : Fs.GetDirectory(Subtree);
            if (Dir == null)
                throw new ArgumentException($"Path '{Subtree}' not found in filesystem");

            Walk(Dir, Base, Res.Entries);
            return Res;
        }

        private static void Walk(Dir Dir, string Prefix, List<Entry> Res)
        {
            foreach (var Item in Dir.Items)
            {
                var Full = Normalize(Item.Filename);
                var Rel = (Prefix.Length == 0) ? Full : Full.Substring(Prefix.Length).TrimStart('/');
                if (Rel.Length == 0)
                    continue;

                var E = new Entry
                {
                    Path = Rel,
                    User = Item.User,
                    Group = Item.Group,
                    Mode = Item.Mode,
                    Xattrs = (Item.Xattrs.Count > 0) ? new Dictionary<string, byte[]>(Item.Xattrs) : null
                };

                switch (Item)
                {
                    case Dir D:
                        E.Type = FilesystemItemType.Directory;
                        E.IsDir = true;
                        Res.Add(E);
                        Walk(D, Prefix, Res);
                        break;
                    case SymLink S:
                        E.Type = FilesystemItemType.SymLink;
                        E.IsLink = true;
                        E.Target = S.Target;
                        Res.Add(E);
                        break;
                    case File F:
                        E.Type = FilesystemItemType.File;
                        E.Content = F.Content ?? Array.Empty<byte>();
                        Res.Add(E);
                        break;
                    case CChar C:
                        E.Type = FilesystemItemType.Character;
                        E.Major = C.Major;
                        E.Minor = C.Minor;
                        Res.Add(E);
                        break;
                    case Block B:
                        E.Type = FilesystemItemType.Block;
                        E.Major = B.Major;
                        E.Minor = B.Minor;
                        Res.Add(E);
                        break;
                    case Fifo:
                        E.Type = FilesystemItemType.Fifo;
                        Res.Add(E);
                        break;
                    case Socket:
                        E.Type = FilesystemItemType.Socket;
                        Res.Add(E);
                        break;
                }
            }
        }

        public byte[] ToFile()
        {
            var B = new Filesystem.Tar.TarFsBuilder();
            B.Directory(DataDir, 0, 0, 0x1ED);
            B.File(ControlName, Metadata.ToControl(), 0, 0, 0x1A4);

            foreach (var E in Entries)
            {
                var P = DataDir + "/" + E.Path;
                switch (E.Type)
                {
                    case FilesystemItemType.Directory:
                        B.Directory(P, E.User, E.Group, E.Mode);
                        break;
                    case FilesystemItemType.SymLink:
                        B.SymLink(P, E.Target, E.User, E.Group, E.Mode);
                        break;
                    case FilesystemItemType.Character:
                        B.Char(P, E.Major, E.Minor, E.User, E.Group, E.Mode);
                        break;
                    case FilesystemItemType.Block:
                        B.Block(P, E.Major, E.Minor, E.User, E.Group, E.Mode);
                        break;
                    case FilesystemItemType.Fifo:
                        B.Fifo(P, E.User, E.Group, E.Mode);
                        break;
                    case FilesystemItemType.Socket:
                        B.Socket(P, E.User, E.Group, E.Mode);
                        break;
                    default:
                        B.File(P, E.Content ?? Array.Empty<byte>(), E.User, E.Group, E.Mode);
                        break;
                }

                if (E.Xattrs != null)
                    foreach (var X in E.Xattrs)
                        B.Xattr(P, X.Key, X.Value);
            }

            var Tar = B.GetFilesystemImage();
            return ImageFormat.Compressors.Gzip.CompressWithHeader(Tar);
        }

        // ---- read ----

        public static NyaPackage Open(byte[] File)
        {
            var Res = new NyaPackage();
            var Tar = ImageFormat.Compressors.Gzip.Decompress(File);
            var Reader = new Filesystem.Tar.TarFsReader(Tar);

            var Control = Reader.Read(ControlName);
            if (Control != null)
                Res.Metadata = PackageMetadata.Parse(Control);

            ReadDir(Reader, DataDir, Res.Entries);
            return Res;
        }

        private static void ReadDir(Filesystem.Tar.TarFsReader Reader, string Path, List<Entry> Res)
        {
            var Items = Reader.ReadDir(Path);
            if (Items == null)
                return;

            foreach (var I in Items)
            {
                var Prefix = DataDir + "/";
                var Rel = I.Path.StartsWith(Prefix, StringComparison.Ordinal) ? I.Path.Substring(Prefix.Length) : I.Path;
                if (Rel.Length == 0)
                    continue;

                var E = new Entry
                {
                    Path = Rel,
                    Type = I.NodeType,
                    User = I.User,
                    Group = I.Group,
                    Mode = I.Mode & 0xFFF,
                    Xattrs = I.Xattrs
                };

                switch (I.NodeType)
                {
                    case FilesystemItemType.Directory:
                        E.IsDir = true;
                        Res.Add(E);
                        ReadDir(Reader, I.Path, Res);
                        break;
                    case FilesystemItemType.SymLink:
                        E.IsLink = true;
                        E.Target = Reader.ReadLink(I.Path);
                        Res.Add(E);
                        break;
                    case FilesystemItemType.Character:
                    case FilesystemItemType.Block:
                        {
                            var Dev = Reader.ReadDevice(I.Path);
                            E.Major = Dev?.Major ?? 0;
                            E.Minor = Dev?.Minor ?? 0;
                            Res.Add(E);
                        }
                        break;
                    case FilesystemItemType.Fifo:
                    case FilesystemItemType.Socket:
                        Res.Add(E);
                        break;
                    default:
                        E.Type = FilesystemItemType.File;
                        E.Content = Reader.Read(I.Path) ?? Array.Empty<byte>();
                        Res.Add(E);
                        break;
                }
            }
        }

        // ---- selection / install ----

        public bool Matches(LinuxFilesystem Target)
        {
            var WantArch = NormalizeArch(Metadata.Arch);
            if ((WantArch.Length > 0) && (Target.Info.Architecture != ImageFormat.Types.CPU.IH_ARCH_INVALID))
            {
                var HaveArch = NormalizeArch(Target.Info.Architecture.ToString().Replace("IH_ARCH_", ""));
                if (!string.Equals(WantArch, HaveArch, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var WantOs = Metadata.Os.Trim().ToLowerInvariant();
            if ((WantOs.Length > 0) && (Target.Info.OperatingSystem != ImageFormat.Types.OS.IH_OS_INVALID))
            {
                var HaveOs = Target.Info.OperatingSystem.ToString().Replace("IH_OS_", "").ToLowerInvariant();
                if (!string.Equals(WantOs, HaveOs, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        /// <summary>Adds data/ entries under BasePath, overwriting existing paths. Returns count of installed items.</summary>
        public int AddTo(LinuxFilesystem Target, string BasePath = "/")
        {
            var Base = Normalize(BasePath);
            int Count = 0;

            foreach (var E in Entries)
            {
                var Path = (Base.Length == 0) ? E.Path : Base + "/" + E.Path;

                if (E.Type == FilesystemItemType.Directory)
                {
                    EnsureDir(Target, Path, E);
                    Count++;
                    continue;
                }

                var ParentPath = ParentOf(Path);
                var Parent = EnsureDir(Target, ParentPath, null);
                var Name = Path.Substring(Path.LastIndexOf('/') + 1);

                var Old = Parent.Items.Find(I => string.Equals(ShortName(I.Filename), Name, StringComparison.Ordinal));
                if (Old != null)
                    Parent.Items.Remove(Old);

                FilesystemItem Item;
                switch (E.Type)
                {
                    case FilesystemItemType.SymLink:
                        Item = new SymLink(Path, E.User, E.Group, E.Mode, E.Target);
                        break;
                    case FilesystemItemType.Character:
                        Item = new CChar(Path, E.User, E.Group, E.Mode) { Major = E.Major, Minor = E.Minor };
                        break;
                    case FilesystemItemType.Block:
                        Item = new Block(Path, E.User, E.Group, E.Mode) { Major = E.Major, Minor = E.Minor };
                        break;
                    case FilesystemItemType.Fifo:
                        Item = new Fifo(Path, E.User, E.Group, E.Mode);
                        break;
                    case FilesystemItemType.Socket:
                        Item = new Socket(Path, E.User, E.Group, E.Mode);
                        break;
                    default:
                        Item = new File(Path, E.User, E.Group, E.Mode, E.Content ?? Array.Empty<byte>());
                        break;
                }

                if (E.Xattrs != null)
                    foreach (var X in E.Xattrs)
                        Item.Xattrs[X.Key] = X.Value;

                Parent.Items.Add(Item);
                Count++;
            }

            return Count;
        }

        private static Dir EnsureDir(LinuxFilesystem Target, string Path, Entry Meta)
        {
            Path = Normalize(Path);
            if (Path.Length == 0)
                return Target.Fs.Root;

            var Cur = Target.Fs.Root;
            var Parts = Path.Split('/');
            var Acc = "";

            foreach (var P in Parts)
            {
                Acc = (Acc.Length == 0) ? P : Acc + "/" + P;
                var Next = Cur.Items.Find(I => (I is Dir) && string.Equals(ShortName(I.Filename), P, StringComparison.Ordinal)) as Dir;
                if (Next == null)
                {
                    Next = new Dir(Acc, Meta?.User ?? 0, Meta?.Group ?? 0, Meta?.Mode ?? 0x1ED);
                    if ((Meta != null) && (Acc == Path) && (Meta.Xattrs != null))
                        foreach (var X in Meta.Xattrs)
                            Next.Xattrs[X.Key] = X.Value;
                    Cur.Items.Add(Next);
                }
                Cur = Next;
            }

            return Cur;
        }

        private static string ShortName(string Path)
        {
            int Idx = Path.LastIndexOf('/');
            return (Idx < 0) ? Path : Path.Substring(Idx + 1);
        }

        private static string ParentOf(string Path)
        {
            int Idx = Path.LastIndexOf('/');
            return (Idx <= 0) ? "" : Path.Substring(0, Idx);
        }

        private static string Normalize(string Path)
        {
            var P = (Path ?? "").Replace('\\', '/');
            while (P.StartsWith("/"))
                P = P.Substring(1);
            return P.TrimEnd('/');
        }

        private static string NormalizeArch(string Arch)
        {
            var A = (Arch ?? "").Trim().ToLowerInvariant();
            switch (A)
            {
                case "aarch64": return "arm64";
                case "amd64":
                case "x64": return "x86_64";
                case "i486":
                case "i586":
                case "i686": return "i386";
                default: return A;
            }
        }
    }
}
