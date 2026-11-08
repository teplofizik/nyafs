using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.EroFs
{
    internal sealed partial class EroFsReader : IFilesystemReader, IRootFsInfo
    {
        private sealed class Inode
        {
            public ulong Nid;
            public int Offset;
            public bool Extended;
            public int Layout;
            public int InodeSize;
            public int XattrSize;
            public ushort Mode;
            public ulong Size;
            public uint RawU;
            public uint User;
            public uint Group;
            public DateTime Modified;
            public bool Compressed;
        }

        private sealed class Node
        {
            public string Name;
            public string Path;
            public Inode Inode;
            public readonly List<Node> Children = new List<Node>();
        }

        private readonly byte[] Data;
        private readonly int BlockSize;
        private readonly int MetaBlockAddress;
        private readonly long Blocks;
        private readonly Dictionary<string, Node> Nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly Node Root;

        public long VolumeBytes => Blocks * BlockSize;

        public EroFsReader(byte[] Data)
        {
            this.Data = Data ?? throw new ArgumentNullException(nameof(Data));

            if (!EroFsShared.IsEroFs(Data))
                throw new InvalidOperationException("Not an EROFS image");

            int Sb = EroFsShared.SuperblockOffset;
            BlockSize = 1 << Data[Sb + 12];
            uint RootNid = EroFsShared.ReadU16(Data, Sb + 14);
            Blocks = EroFsShared.ReadU32(Data, Sb + 36);
            MetaBlockAddress = (int)EroFsShared.ReadU32(Data, Sb + 40);

            Root = new Node
            {
                Name = "",
                Path = "",
                Inode = ReadInode(RootNid)
            };
            Nodes[""] = Root;
            ReadDirectory(Root, new HashSet<ulong>());
        }

        public byte[] Read(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || IsDirectory(Node.Inode))
                return null;

            return ReadInodeData(Node.Inode);
        }

        public string ReadLink(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || ((Node.Inode.Mode & 0xF000) != 0xA000))
                return null;

            return Encoding.UTF8.GetString(ReadInodeData(Node.Inode));
        }

        public DeviceInfo ReadDevice(string Path)
        {
            var Node = Find(Path);
            if (Node == null)
                return null;

            uint Type = (uint)(Node.Inode.Mode & 0xF000);
            if ((Type != 0x2000) && (Type != 0x6000))
                return null;

            uint Dev = Node.Inode.RawU;
            uint Major = (Dev & 0xFFF00) >> 8;
            uint Minor = (Dev & 0xFF) | ((Dev >> 12) & 0xFFF00);
            return new DeviceInfo(Major, Minor);
        }

        public FilesystemEntry[] ReadDir(string Path)
        {
            var Node = Find(Path);
            if ((Node == null) || !IsDirectory(Node.Inode))
                return new FilesystemEntry[0];

            var Result = new List<FilesystemEntry>();
            foreach (var Child in Node.Children)
            {
                Result.Add(new FilesystemEntry(
                    TypeOf(Child.Inode),
                    ExFatPath.Combine(ExFatPath.Normalize(Path), Child.Name),
                    Child.Inode.User,
                    Child.Inode.Group,
                    (uint)(Child.Inode.Mode & 0xFFF),
                    IsDirectory(Child.Inode) ? 0u : (uint)Math.Min(Child.Inode.Size, UInt32.MaxValue)));
            }

            return Result.ToArray();
        }

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            User = Root.Inode.User;
            Group = Root.Inode.Group;
            Mode = (uint)(Root.Inode.Mode & 0xFFF);
            return true;
        }

        private Node Find(string Path)
        {
            var Normalized = ExFatPath.Normalize(Path);
            return Nodes.TryGetValue(Normalized, out Node Result) ? Result : null;
        }

        private void ReadDirectory(Node Directory, HashSet<ulong> Visited)
        {
            if (!Visited.Add(Directory.Inode.Nid))
                return;

            var Bytes = ReadInodeData(Directory.Inode);
            if (Bytes.Length < 12)
                return;

            int FirstName = (int)EroFsShared.ReadU16(Bytes, 8);
            int Count = (FirstName > 0) ? (FirstName / 12) : 0;

            for (int i = 0; i < Count; i++)
            {
                int Offset = i * 12;
                if (Offset + 12 > FirstName)
                    break;

                ulong Nid = EroFsShared.ReadU64(Bytes, Offset);
                int NameOffset = (int)EroFsShared.ReadU16(Bytes, Offset + 8);
                int NameEnd = (i + 1 < Count) ? (int)EroFsShared.ReadU16(Bytes, (i + 1) * 12 + 8) : Bytes.Length;

                if ((NameOffset < FirstName) || (NameOffset >= Bytes.Length))
                    continue;

                NameEnd = Math.Min(NameEnd, Bytes.Length);
                var Name = Encoding.UTF8.GetString(Bytes, NameOffset, Math.Max(0, NameEnd - NameOffset)).TrimEnd('\0');
                if (String.IsNullOrEmpty(Name) || (Name == ".") || (Name == ".."))
                    continue;

                var ChildInode = ReadInode(Nid);
                var Child = new Node
                {
                    Name = Name,
                    Path = ExFatPath.Combine(Directory.Path, Name),
                    Inode = ChildInode
                };

                Directory.Children.Add(Child);
                Nodes[Child.Path] = Child;

                if (IsDirectory(ChildInode))
                    ReadDirectory(Child, Visited);
            }
        }

        private Inode ReadInode(ulong Nid)
        {
            int Offset = MetaBlockAddress * BlockSize + (int)(Nid * 32);
            if (Offset + 32 > Data.Length)
                throw new InvalidOperationException($"EROFS: inode {Nid} is out of image");

            ushort Format = (ushort)EroFsShared.ReadU16(Data, Offset);
            ushort XattrCount = (ushort)EroFsShared.ReadU16(Data, Offset + 2);
            bool Extended = (Format & 1) != 0;
            int Layout = (Format >> 1) & 7;
            int InodeSize = Extended ? 64 : 32;
            int XattrSize = XattrCount == 0 ? 0 : 12 + 4 * (XattrCount - 1);

            var Result = new Inode
            {
                Nid = Nid,
                Offset = Offset,
                Extended = Extended,
                Layout = Layout,
                InodeSize = InodeSize,
                XattrSize = XattrSize,
                Mode = (ushort)EroFsShared.ReadU16(Data, Offset + 4),
                Compressed = (Layout == 1) || (Layout == 3)
            };

            if (Extended)
            {
                Result.Size = EroFsShared.ReadU64(Data, Offset + 8);
                Result.RawU = EroFsShared.ReadU32(Data, Offset + 16);
                Result.User = EroFsShared.ReadU32(Data, Offset + 24);
                Result.Group = EroFsShared.ReadU32(Data, Offset + 28);
                Result.Modified = FromUnix(EroFsShared.ReadU64(Data, Offset + 32));
            }
            else
            {
                Result.Size = EroFsShared.ReadU32(Data, Offset + 8);
                Result.RawU = EroFsShared.ReadU32(Data, Offset + 16);
                Result.User = EroFsShared.ReadU16(Data, Offset + 24);
                Result.Group = EroFsShared.ReadU16(Data, Offset + 26);
                Result.Modified = DateTime.Now;
            }

            return Result;
        }

        private byte[] ReadInodeData(Inode Inode)
        {
            if (Inode.Compressed)
                return ReadCompressedInodeData(Inode);

            if (Inode.Layout == 4)
                return ReadChunkBasedInodeData(Inode);

            if (Inode.Size == 0)
                return Array.Empty<byte>();

            if (Inode.Size > Int32.MaxValue)
                throw new InvalidOperationException("EROFS: file is too large");

            var Result = new byte[(int)Inode.Size];

            if (Inode.Layout == 0)
            {
                long Source = (long)Inode.RawU * BlockSize;
                long Available = Math.Min((long)Result.Length, Data.Length - Source);
                if (Available > 0)
                    Array.Copy(Data, Source, Result, 0, Available);
                return Result;
            }

            if (Inode.Layout == 2)
            {
                int FullBlocks = Result.Length / BlockSize;
                int Tail = Result.Length % BlockSize;
                long Source = (long)Inode.RawU * BlockSize;

                if (FullBlocks > 0)
                {
                    int FullSize = FullBlocks * BlockSize;
                    long Available = Math.Min(FullSize, Data.Length - Source);
                    if (Available > 0)
                        Array.Copy(Data, Source, Result, 0, Available);
                }

                if (Tail > 0)
                {
                    long Inline = (long)Inode.Offset + Inode.InodeSize + Inode.XattrSize;
                    long Available = Math.Min(Tail, Data.Length - Inline);
                    if (Available > 0)
                        Array.Copy(Data, Inline, Result, FullBlocks * BlockSize, Available);
                }

                return Result;
            }

            NyaFs.Log.Warning(0, $"EROFS: inode {Inode.Nid} has unsupported data layout {Inode.Layout}");
            return Array.Empty<byte>();
        }

        private static bool IsDirectory(Inode Inode) => (Inode.Mode & 0xF000) == 0x4000;

        private static FilesystemItemType TypeOf(Inode Inode)
        {
            switch (Inode.Mode & 0xF000)
            {
                case 0x4000: return FilesystemItemType.Directory;
                case 0x8000: return FilesystemItemType.File;
                case 0xA000: return FilesystemItemType.SymLink;
                case 0x1000: return FilesystemItemType.Fifo;
                case 0x2000: return FilesystemItemType.Character;
                case 0x6000: return FilesystemItemType.Block;
                case 0xC000: return FilesystemItemType.Socket;
                default: return FilesystemItemType.Unknown;
            }
        }

        private static DateTime FromUnix(ulong Seconds)
        {
            try
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Seconds);
            }
            catch
            {
                return DateTime.Now;
            }
        }

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
