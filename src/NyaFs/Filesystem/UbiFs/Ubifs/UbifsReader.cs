using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NyaIO.Data;
using NyaFs.Filesystem.Universal;

namespace NyaFs.Filesystem.UbiFs.Ubifs
{
    /// <summary>
    /// UBIFS reader: reads the superblock and master node, walks the indexing B-tree,
    /// collects inode / dent / data nodes and exposes them as a filesystem.
    /// </summary>
    public class UbifsReader : IFilesystemReader, IRootFsInfo
    {
        public readonly UbiFsReader Ubi;
        public readonly UbiVolume Volume;

        public byte[] Superblock;
        public byte[] Master;

        public readonly List<UbifsNode> Inodes = new List<UbifsNode>();
        public readonly List<UbifsNode> Dents = new List<UbifsNode>();
        public readonly List<UbifsNode> Datas = new List<UbifsNode>();

        private readonly Dictionary<uint, byte[]> LebCache = new Dictionary<uint, byte[]>();
        private readonly Dictionary<uint, UbifsNode> InodeByInum = new Dictionary<uint, UbifsNode>();
        private readonly Dictionary<uint, List<UbifsNode>> DentsByParent = new Dictionary<uint, List<UbifsNode>>();
        private readonly Dictionary<uint, List<UbifsNode>> DataByInum = new Dictionary<uint, List<UbifsNode>>();

        public const uint RootInum = 1;

        public UbifsReader(UbiFsReader Ubi, UbiVolume Volume)
        {
            this.Ubi = Ubi;
            this.Volume = Volume;
            Init();
        }

        public int LebSize => Ubi.LebSize;

        private byte[] Leb(uint Lnum)
        {
            if (!LebCache.TryGetValue(Lnum, out var D))
            {
                D = Ubi.ReadLeb(Volume.Id, Lnum);
                LebCache[Lnum] = D;
            }
            return D;
        }

        private void Init()
        {
            Superblock = Leb(0);
            Master = Leb(1);

            uint RootLnum = BitConverter.ToUInt32(Master, 0x30);
            uint RootOffs = BitConverter.ToUInt32(Master, 0x34);

            TraverseIndex(RootLnum, RootOffs, 0);
            BuildIndexes();
        }

        private void TraverseIndex(uint Lnum, uint Offs, int Depth)
        {
            if (Depth > 64)
                throw new InvalidOperationException("UBIFS index tree too deep");

            var Node = new UbifsIndexNode(Leb(Lnum), (int)Offs, Lnum);

            for (int i = 0; i < Node.ChildCnt; i++)
            {
                Node.GetBranch(i, out uint BLnum, out uint BOffs, out _, out _);

                var Target = new UbifsNode(Leb(BLnum), (int)BOffs, BLnum, true);

                if (Target.Type == UbifsNodeType.Idx)
                    TraverseIndex(BLnum, BOffs, Depth + 1);
                else
                    Collect(Target);
            }
        }

        private void Collect(UbifsNode Node)
        {
            switch (Node.Type)
            {
                case UbifsNodeType.Ino: Inodes.Add(Node); break;
                case UbifsNodeType.Dent: Dents.Add(Node); break;
                case UbifsNodeType.Data: Datas.Add(Node); break;
            }
        }

        private void BuildIndexes()
        {
            foreach (var N in Inodes)
                InodeByInum[N.Key.Inum] = N;

            foreach (var N in Dents)
            {
                uint Parent = N.Key.Inum;
                if (!DentsByParent.TryGetValue(Parent, out var L))
                    DentsByParent[Parent] = L = new List<UbifsNode>();
                L.Add(N);
            }

            foreach (var N in Datas)
            {
                uint Inum = N.Key.Inum;
                if (!DataByInum.TryGetValue(Inum, out var L))
                    DataByInum[Inum] = L = new List<UbifsNode>();
                L.Add(N);
            }
        }

        // ---- inode / dent / data accessors ----

        private static uint InoMode(UbifsNode N) => BitConverter.ToUInt32(N.Data, N.Off + 0x68);
        private static ulong InoSize(UbifsNode N) => BitConverter.ToUInt64(N.Data, N.Off + 0x30);
        private static uint InoUid(UbifsNode N) => BitConverter.ToUInt32(N.Data, N.Off + 0x60);
        private static uint InoGid(UbifsNode N) => BitConverter.ToUInt32(N.Data, N.Off + 0x64);
        private static uint InoDataLen(UbifsNode N) => BitConverter.ToUInt32(N.Data, N.Off + 0x70);
        private static byte[] InoInlineData(UbifsNode N) => N.Data.ReadArray(N.Off + 0xA0, InoDataLen(N));

        private static ulong DentInum(UbifsNode N) => BitConverter.ToUInt64(N.Data, N.Off + 0x28);
        private static byte DentType(UbifsNode N) => N.Data[N.Off + 0x31];
        private static ushort DentNlen(UbifsNode N) => BitConverter.ToUInt16(N.Data, N.Off + 0x32);
        private static string DentName(UbifsNode N) => Encoding.UTF8.GetString(N.Data, N.Off + 0x38, DentNlen(N));

        private static uint DataSize(UbifsNode N) => BitConverter.ToUInt32(N.Data, N.Off + 0x28);
        private static ushort DataCompr(UbifsNode N) => BitConverter.ToUInt16(N.Data, N.Off + 0x2C);
        private static byte[] DataRaw(UbifsNode N) => N.Data.ReadArray(N.Off + 0x30, (int)(N.Len - 0x30));

        private static Universal.Types.FilesystemItemType ModeToType(uint Mode) => (Mode & 0xF000) switch
        {
            0x4000 => Universal.Types.FilesystemItemType.Directory,
            0x8000 => Universal.Types.FilesystemItemType.File,
            0xA000 => Universal.Types.FilesystemItemType.SymLink,
            0x2000 => Universal.Types.FilesystemItemType.Character,
            0x6000 => Universal.Types.FilesystemItemType.Block,
            0x1000 => Universal.Types.FilesystemItemType.Fifo,
            0xC000 => Universal.Types.FilesystemItemType.Socket,
            _ => Universal.Types.FilesystemItemType.Unknown
        };

        private UbifsNode Inode(uint Inum) => InodeByInum.TryGetValue(Inum, out var N) ? N : null;

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            var N = Inode(RootInum);
            if (N == null)
            {
                User = Group = Mode = 0;
                return false;
            }

            User = InoUid(N);
            Group = InoGid(N);
            Mode = InoMode(N) & 0xFFF;
            return true;
        }

        // ---- path resolution ----

        private uint GetInumByPath(string Path)
        {
            if ((Path == ".") || (Path == "/") || (Path.Length == 0))
                return RootInum;

            if (Path[0] == '/')
                Path = Path.Substring(1);

            uint Inum = RootInum;

            foreach (var Part in Path.Split('/'))
            {
                if (Part.Length == 0)
                    continue;

                uint Next = 0;
                bool Found = false;

                if (DentsByParent.TryGetValue(Inum, out var L))
                {
                    foreach (var D in L)
                    {
                        if (DentName(D) == Part)
                        {
                            Next = (uint)DentInum(D);
                            Found = true;
                            break;
                        }
                    }
                }

                if (!Found)
                    return 0;

                Inum = Next;
            }

            return Inum;
        }

        // ---- IFilesystemReader ----

        public FilesystemEntry[] ReadDir(string Path)
        {
            uint Inum = GetInumByPath(Path);
            var Dir = Inode(Inum);

            if ((Dir == null) || (ModeToType(InoMode(Dir)) != Universal.Types.FilesystemItemType.Directory))
                return null;

            var Res = new List<FilesystemEntry>();

            if (DentsByParent.TryGetValue(Inum, out var L))
            {
                foreach (var D in L)
                {
                    var Child = Inode((uint)DentInum(D));
                    if (Child == null)
                        continue;

                    uint Mode = InoMode(Child);
                    Res.Add(new FilesystemEntry(ModeToType(Mode), Universal.Helper.FsHelper.CombinePath(Path, DentName(D)),
                        InoUid(Child), InoGid(Child), Mode & 0xFFF, Convert.ToUInt32(InoSize(Child))));
                }
            }

            return Res.ToArray();
        }

        public byte[] Read(string Path)
        {
            uint Inum = GetInumByPath(Path);
            var Node = Inode(Inum);

            if ((Node == null) || (ModeToType(InoMode(Node)) != Universal.Types.FilesystemItemType.File))
                return null;

            return ReadFileData(Inum, Node);
        }

        public string ReadLink(string Path)
        {
            uint Inum = GetInumByPath(Path);
            var Node = Inode(Inum);

            if ((Node == null) || (ModeToType(InoMode(Node)) != Universal.Types.FilesystemItemType.SymLink))
                return null;

            // Symlink target is stored as the inode's inline data.
            return Encoding.UTF8.GetString(InoInlineData(Node));
        }

        public Universal.Types.DeviceInfo ReadDevice(string Path)
        {
            uint Inum = GetInumByPath(Path);
            var Node = Inode(Inum);

            if (Node == null)
                return null;

            var T = ModeToType(InoMode(Node));
            if ((T != Universal.Types.FilesystemItemType.Character) && (T != Universal.Types.FilesystemItemType.Block))
                return null;

            var D = InoInlineData(Node);
            uint Dev = BitConverter.ToUInt32(D, 0);
            uint Major = (Dev >> 8) & 0xFFF;
            uint Minor = (Dev & 0xFF) | ((Dev >> 12) & 0xFFF00);
            return new Universal.Types.DeviceInfo(Major, Minor);
        }

        private byte[] ReadFileData(uint Inum, UbifsNode Node)
        {
            ulong Size = InoSize(Node);
            var Res = new byte[Size];

            if (DataByInum.TryGetValue(Inum, out var L))
            {
                foreach (var D in L)
                {
                    long BlockOffset = (long)D.Key.Block * 0x1000;
                    if (BlockOffset >= (long)Size)
                        continue;

                    var Data = DecompressData(DataSize(D), DataCompr(D), DataRaw(D));
                    long ToCopy = Math.Min(Data.Length, (long)Size - BlockOffset);
                    Array.Copy(Data, 0, Res, BlockOffset, ToCopy);
                }
            }

            return Res;
        }

        private byte[] DecompressData(uint Size, ushort Compr, byte[] Raw)
        {
            switch (Compr)
            {
                case 0: return Raw;
                case 1: return NyaLZO.LZO1xDecompressor.Decompress(Raw);
                case 2: return Inflate(Raw);
                case 3: return ImageFormat.Compressors.ZStd.Decompress(Raw);
                default: throw new InvalidOperationException($"Unsupported UBIFS compression {Compr}");
            }
        }

        /// <summary>UBIFS zlib = raw deflate stream.</summary>
        private static byte[] Inflate(byte[] Raw)
        {
            using (var Input = new MemoryStream(Raw))
            using (var Deflate = new System.IO.Compression.DeflateStream(Input, System.IO.Compression.CompressionMode.Decompress))
            using (var Output = new MemoryStream())
            {
                Deflate.CopyTo(Output);
                return Output.ToArray();
            }
        }
    }
}
