using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;

namespace NyaFs.Filesystem.Tar
{
    /// <summary>
    /// Builder for ustar tar archives (with GNU long name/link fallback).
    /// </summary>
    public class TarFsBuilder : IFilesystemBuilder
    {
        private const int BlockingFactor = 20; // 20 * 512 = 10240 (standard tar record size)

        private class Entry
        {
            public string Path;
            public uint User;
            public uint Group;
            public uint Mode;
            public FilesystemItemType Type;
            public byte[] Content;
            public string Target;
            public uint Major;
            public uint Minor;
        }

        private readonly List<Entry> Files = new List<Entry>();

        /// <summary>
        /// Get builded filesystem image
        /// </summary>
        public byte[] GetFilesystemImage()
        {
            using (var Stream = new MemoryStream())
            {
                foreach (var E in Files)
                    WriteEntry(Stream, E);

                // end-of-archive: two zero blocks
                Stream.Write(new byte[1024], 0, 1024);

                // pad up to the tar record size
                int Record = 512 * BlockingFactor;
                long Rem = Stream.Length % Record;
                if (Rem != 0)
                {
                    int Pad = (int)(Record - Rem);
                    Stream.Write(new byte[Pad], 0, Pad);
                }

                return Stream.ToArray();
            }
        }

        private static string Normalize(string Path)
        {
            if (string.IsNullOrEmpty(Path)) return "";

            Path = Path.Replace('\\', '/');
            while (Path.StartsWith("./")) Path = Path.Substring(2);
            if (Path.StartsWith("/")) Path = Path.Substring(1);
            Path = Path.TrimEnd('/');

            return (Path == ".") ? "" : Path;
        }

        private static Types.TarFileType MapType(FilesystemItemType Type)
        {
            switch (Type)
            {
                case FilesystemItemType.Directory: return Types.TarFileType.Directory;
                case FilesystemItemType.SymLink: return Types.TarFileType.SymLink;
                case FilesystemItemType.Character: return Types.TarFileType.Character;
                case FilesystemItemType.Block: return Types.TarFileType.Block;
                case FilesystemItemType.Fifo: return Types.TarFileType.Fifo;
                default: return Types.TarFileType.Regular;
            }
        }

        /// <summary>
        /// Try to fit a name into ustar name/prefix fields.
        /// </summary>
        private static bool Fits(string Name, out string Prefix, out string Base)
        {
            Prefix = "";
            Base = Name;

            if (Name.Length <= 100) return true;

            for (int i = Name.Length - 1; i > 0; i--)
            {
                if (Name[i] != '/') continue;

                var P = Name.Substring(0, i);
                var B = Name.Substring(i + 1);

                if ((P.Length <= 155) && (B.Length <= 100))
                {
                    Prefix = P;
                    Base = B;
                    return true;
                }
            }

            Prefix = "";
            Base = "";
            return false;
        }

        private static void WritePadded(Stream Stream, byte[] Data)
        {
            Stream.Write(Data, 0, Data.Length);

            int Pad = (int)((512 - (Data.Length % 512)) % 512);
            if (Pad > 0)
                Stream.Write(new byte[Pad], 0, Pad);
        }

        private static Types.TarHeader CreateHeader(string Name, string Prefix, Types.TarFileType Type, string Link, long Size, uint User, uint Group, uint Mode, uint Major, uint Minor)
        {
            var Header = new Types.TarHeader();

            Header.Name = Name;
            Header.Prefix = Prefix;
            Header.Mode = Mode & 0xFFF;
            Header.Uid = User;
            Header.Gid = Group;
            Header.Size = Size;
            Header.MTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Header.TypeFlagByte = (byte)Type;
            Header.LinkName = Link;
            Header.Magic = "ustar";
            Header.Version = "00";
            Header.UserName = "root";
            Header.GroupName = "root";
            Header.DevMajor = Major;
            Header.DevMinor = Minor;
            Header.WriteChecksum();

            return Header;
        }

        private static void WriteGnuLong(Stream Stream, Types.TarFileType Type, string Value)
        {
            var Data = Encoding.UTF8.GetBytes(Value + "\0");
            var Header = CreateHeader("././@LongLink", "", Type, "", Data.Length, 0, 0, 0, 0, 0);

            Stream.Write(Header.getPacket(), 0, Types.TarHeader.HeaderSize);
            WritePadded(Stream, Data);
        }

        private void WriteEntry(Stream Stream, Entry E)
        {
            var Name = Normalize(E.Path);
            if (E.Type == FilesystemItemType.Directory) Name += "/";

            var Type = MapType(E.Type);
            var Link = (E.Type == FilesystemItemType.SymLink) ? (E.Target ?? "") : "";
            var Content = (E.Type == FilesystemItemType.File) ? (E.Content ?? new byte[0]) : new byte[0];

            if (!Fits(Name, out var Prefix, out var Base))
            {
                WriteGnuLong(Stream, Types.TarFileType.GnuLongName, Name);
                Prefix = "";
                Base = Name.Substring(0, Math.Min(Name.Length, 100));
            }

            if (Link.Length > 100)
            {
                WriteGnuLong(Stream, Types.TarFileType.GnuLongLink, Link);
                Link = Link.Substring(0, 100);
            }

            var Header = CreateHeader(Base, Prefix, Type, Link, Content.Length, E.User, E.Group, E.Mode, E.Major, E.Minor);

            Stream.Write(Header.getPacket(), 0, Types.TarHeader.HeaderSize);
            WritePadded(Stream, Content);
        }

        /// <summary>
        /// Create block device
        /// </summary>
        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode)
        {
            Files.Add(new Entry { Path = Path, Type = FilesystemItemType.Block, User = User, Group = Group, Mode = Mode, Major = Major, Minor = Minor });
        }

        /// <summary>
        /// Create char device
        /// </summary>
        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode)
        {
            Files.Add(new Entry { Path = Path, Type = FilesystemItemType.Character, User = User, Group = Group, Mode = Mode, Major = Major, Minor = Minor });
        }

        /// <summary>
        /// Create directory
        /// </summary>
        public void Directory(string Path, uint User, uint Group, uint Mode)
        {
            if ((Path == "") || (Path == "/")) return;

            Files.Add(new Entry { Path = Path, Type = FilesystemItemType.Directory, User = User, Group = Group, Mode = Mode });
        }

        /// <summary>
        /// Create file
        /// </summary>
        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode)
        {
            Files.Add(new Entry { Path = Path, Type = FilesystemItemType.File, User = User, Group = Group, Mode = Mode, Content = Content });
        }

        /// <summary>
        /// Create fifo
        /// </summary>
        public void Fifo(string Path, uint User, uint Group, uint Mode)
        {
            Files.Add(new Entry { Path = Path, Type = FilesystemItemType.Fifo, User = User, Group = Group, Mode = Mode });
        }

        /// <summary>
        /// Create socket (tar has no socket type, stored as empty regular file)
        /// </summary>
        public void Socket(string Path, uint User, uint Group, uint Mode)
        {
            Files.Add(new Entry { Path = Path, Type = FilesystemItemType.File, User = User, Group = Group, Mode = Mode, Content = new byte[0] });
        }

        /// <summary>
        /// Create symlink
        /// </summary>
        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode)
        {
            Files.Add(new Entry { Path = Path, Type = FilesystemItemType.SymLink, User = User, Group = Group, Mode = Mode, Target = Target });
        }
    }
}
