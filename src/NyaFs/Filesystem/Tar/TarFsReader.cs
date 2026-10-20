using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NyaIO.Data;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;

namespace NyaFs.Filesystem.Tar
{
    /// <summary>
    /// Reader for ustar (and GNU long name/link) tar archives.
    /// </summary>
    public class TarFsReader : RawPacket, IFilesystemReader, IRootFsInfo, IRootXattrs
    {
        private class Node
        {
            public string Path;
            public uint User;
            public uint Group;
            public uint Mode;
            public FilesystemItemType Type;
            public long Size;
            public long ContentOffset;
            public string Link;
            public uint Major;
            public uint Minor;
            public Dictionary<string, byte[]> Xattrs;
        }

        private readonly List<Node> Nodes = new List<Node>();

        private bool HasRoot;
        private uint RootUser;
        private uint RootGroup;
        private uint RootMode;
        private Dictionary<string, byte[]> RootXattrs;

        public TarFsReader(byte[] Data) : base(Data) { Init(); }

        public TarFsReader(string Filename) : this(File.ReadAllBytes(Filename)) { }

        private static string Normalize(string Path)
        {
            if (string.IsNullOrEmpty(Path)) return "";

            Path = Path.Replace('\\', '/');
            while (Path.StartsWith("./")) Path = Path.Substring(2);
            if (Path.StartsWith("/")) Path = Path.Substring(1);
            Path = Path.TrimEnd('/');

            return (Path == ".") ? "" : Path;
        }

        private static long Align512(long Size) => (Size + 511) & ~511L;

        private static string ParentOf(string Path)
        {
            int Idx = Path.LastIndexOf('/');
            return (Idx < 0) ? "" : Path.Substring(0, Idx);
        }

        private byte[] ReadData(long Offset, long Size)
        {
            var Res = new byte[Size];
            Array.Copy(Raw, Offset, Res, 0, Size);
            return Res;
        }

        private static string DecodeLongName(byte[] Data)
        {
            int Len = Array.IndexOf(Data, (byte)0);
            if (Len < 0) Len = Data.Length;
            return Encoding.UTF8.GetString(Data, 0, Len);
        }

        private static FilesystemItemType MapType(Types.TarFileType Type)
        {
            switch (Type)
            {
                case Types.TarFileType.Directory: return FilesystemItemType.Directory;
                case Types.TarFileType.SymLink: return FilesystemItemType.SymLink;
                case Types.TarFileType.Character: return FilesystemItemType.Character;
                case Types.TarFileType.Block: return FilesystemItemType.Block;
                case Types.TarFileType.Fifo: return FilesystemItemType.Fifo;
                default: return FilesystemItemType.File;
            }
        }

        private void Init()
        {
            long Offset = 0;
            string LongName = null;
            string LongLink = null;
            Dictionary<string, byte[]> PaxXattrs = null;
            string PaxPath = null;
            string PaxLink = null;

            while (Offset + Types.TarHeader.HeaderSize <= Raw.Length)
            {
                var Header = new Types.TarHeader(Raw, Offset);
                if (Header.IsEmpty) break;

                var Type = (Types.TarFileType)Header.TypeFlagByte;
                long Size = Header.Size;
                long DataOffset = Offset + Types.TarHeader.HeaderSize;

                if (Type == Types.TarFileType.GnuLongName)
                {
                    LongName = DecodeLongName(ReadData(DataOffset, Size));
                    Offset = DataOffset + Align512(Size);
                    continue;
                }

                if (Type == Types.TarFileType.GnuLongLink)
                {
                    LongLink = DecodeLongName(ReadData(DataOffset, Size));
                    Offset = DataOffset + Align512(Size);
                    continue;
                }

                if ((Header.TypeFlagByte == (byte)'x') || (Header.TypeFlagByte == (byte)'g'))
                {
                    ParsePax(ReadData(DataOffset, Size), ref PaxXattrs, ref PaxPath, ref PaxLink);
                    Offset = DataOffset + Align512(Size);
                    continue;
                }

                var Name = PaxPath ?? LongName ?? Header.FullName;
                var Link = PaxLink ?? LongLink ?? Header.LinkName;
                LongName = null;
                LongLink = null;
                PaxPath = null;
                PaxLink = null;

                var Path = Normalize(Name);
                if (Path.Length == 0)
                {
                    // root directory entry ("." / "./")
                    if (MapType(Type) == FilesystemItemType.Directory)
                    {
                        RootUser = Convert.ToUInt32(Header.Uid);
                        RootGroup = Convert.ToUInt32(Header.Gid);
                        RootMode = Convert.ToUInt32(Header.Mode & 0xFFF);
                        RootXattrs = PaxXattrs;
                        HasRoot = true;
                    }
                }
                else
                {
                    Nodes.Add(new Node
                    {
                        Path = Path,
                        User = Convert.ToUInt32(Header.Uid),
                        Group = Convert.ToUInt32(Header.Gid),
                        Mode = Convert.ToUInt32(Header.Mode & 0xFFF),
                        Type = MapType(Type),
                        Size = Size,
                        ContentOffset = DataOffset,
                        Link = Link,
                        Major = Convert.ToUInt32(Header.DevMajor),
                        Minor = Convert.ToUInt32(Header.DevMinor),
                        Xattrs = PaxXattrs
                    });
                }

                PaxXattrs = null;

                Offset = DataOffset + Align512(Size);
            }
        }

        private static void ParsePax(byte[] Body, ref Dictionary<string, byte[]> Xattrs, ref string Path, ref string Link)
        {
            int P = 0;
            while (P < Body.Length)
            {
                int Sp = Array.IndexOf(Body, (byte)' ', P);
                if (Sp < 0) break;

                if (!int.TryParse(Encoding.ASCII.GetString(Body, P, Sp - P), out int Len) || (Len <= 0))
                    break;

                int RecEnd = Math.Min(P + Len, Body.Length);
                int Eq = Array.IndexOf(Body, (byte)'=', Sp + 1, RecEnd - (Sp + 1));
                if (Eq < 0) { P += Len; continue; }

                var Key = Encoding.UTF8.GetString(Body, Sp + 1, Eq - (Sp + 1));
                int ValueLen = RecEnd - (Eq + 1);
                if ((ValueLen > 0) && (Body[RecEnd - 1] == (byte)'\n'))
                    ValueLen--;

                var Val = new byte[ValueLen];
                Array.Copy(Body, Eq + 1, Val, 0, ValueLen);

                if (Key.StartsWith("LIBARCHIVE.xattr.", StringComparison.Ordinal) && (Key.Length > 17))
                {
                    try { Xattrs = Xattrs ?? new Dictionary<string, byte[]>(); Xattrs[Key.Substring(17)] = Convert.FromBase64String(Encoding.ASCII.GetString(Val)); }
                    catch { }
                }
                else if (Key.StartsWith("SCHILY.xattr.", StringComparison.Ordinal) && (Key.Length > 13))
                {
                    Xattrs = Xattrs ?? new Dictionary<string, byte[]>();
                    Xattrs[Key.Substring(13)] = Val;
                }
                else if (Key == "path")
                    Path = Encoding.UTF8.GetString(Val);
                else if (Key == "linkpath")
                    Link = Encoding.UTF8.GetString(Val);

                P += Len;
            }
        }

        private Node Find(string Path)
        {
            var Normalized = Normalize(Path);
            foreach (var N in Nodes)
                if (N.Path == Normalized) return N;
            return null;
        }

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            User = RootUser;
            Group = RootGroup;
            Mode = RootMode;
            return HasRoot;
        }

        public Dictionary<string, byte[]> GetRootXattrs() => RootXattrs;

        /// <summary>
        /// Read file by path
        /// </summary>
        public byte[] Read(string Path)
        {
            var N = Find(Path);
            if ((N == null) || (N.Type != FilesystemItemType.File)) return null;

            return (N.Size > 0) ? ReadData(N.ContentOffset, N.Size) : new byte[0];
        }

        /// <summary>
        /// Read symlink target by path
        /// </summary>
        public string ReadLink(string Path)
        {
            var N = Find(Path);
            return ((N != null) && (N.Type == FilesystemItemType.SymLink)) ? N.Link : null;
        }

        /// <summary>
        /// Read device information by path
        /// </summary>
        public DeviceInfo ReadDevice(string Path)
        {
            var N = Find(Path);
            if (N == null) return null;

            if ((N.Type == FilesystemItemType.Character) || (N.Type == FilesystemItemType.Block))
                return new DeviceInfo(N.Major, N.Minor);

            return null;
        }

        /// <summary>
        /// Read directory content
        /// </summary>
        public FilesystemEntry[] ReadDir(string Path)
        {
            var Res = new List<FilesystemEntry>();
            var Normalized = Normalize(Path);

            foreach (var N in Nodes)
            {
                if (N.Path.Length == 0) continue;
                if (ParentOf(N.Path) == Normalized)
                {
                    var Entry = new FilesystemEntry(N.Type, N.Path, N.User, N.Group, N.Mode, Convert.ToUInt32(N.Size));
                    if (N.Xattrs != null)
                        Entry.Xattrs = N.Xattrs;
                    Res.Add(Entry);
                }
            }

            return Res.ToArray();
        }
    }
}
