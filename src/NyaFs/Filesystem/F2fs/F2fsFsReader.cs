using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;

namespace NyaFs.Filesystem.F2fs
{
    /// <summary>
    /// F2FS reader (partial): superblock, NAT lookup, inode/dentry blocks.
    /// Currently supports regular directory/data blocks; inline dentry and inline data
    /// (used by mkfs/older default trees) are not parsed yet.
    /// </summary>
    public class F2fsFsReader : IFilesystemReader, IRootXattrs
    {
        public readonly F2fsSuperblock Superblock;
        public readonly F2fsCheckpoint Checkpoint;

        private readonly byte[] Raw;
        private readonly int BlockSize;

        // f2fs_dentry_block geometry (4K page, f2fs constants)
        private const int NR_DENTRY = 214;
        private const int NR_INLINE_DENTRY = 182;
        private const int DENTRY_SIZE = 11;
        private const int SLOT_LEN = 8;
        private const int DENTRY_BITMAP = 27;
        private const int DENTRY_BASE = 30;

        // inode layout offsets
        private const int INO_MODE = 0x00;
        private const int INO_INLINE = 0x03;
        private const int INO_SIZE = 0x10;
        private const int INO_ADDR = 0x168;      // i_addr[]
        private const int INO_NID = 0xFD4;       // i_nid[]
        private const int INO_XATTR_NID = 0x4C;  // i_xattr_nid
        private const int INO_FLAGS = 0x50;      // i_flags
        private const uint F2FS_COMPR_FL = 0x4;
        private const int INO_COMPR_BASE = 0x168;  // i_extra_isize.. (extra attrs)
        private const uint COMPRESS_ADDR = 0xFFFFFFFE;
        private const int ADDRS_PER_INODE = 923;
        private const int INLINE_XATTR_ADDRS = 50;   // DEFAULT_INLINE_XATTR_ADDRS
        private const uint XATTR_MAGIC = 0xF2F52011;

        private const byte INLINE_XATTR = 0x01;
        private const byte INLINE_DATA = 0x02;
        private const byte INLINE_DENTRY = 0x04;

        public F2fsFsReader(byte[] Data)
        {
            Raw = Data;
            Superblock = new F2fsSuperblock(Data, 0);
            BlockSize = Superblock.BlockSize;
            Checkpoint = F2fsCheckpoint.Read(Superblock, Data);
        }

        public F2fsFsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }

        public string Version => $"{Superblock.MajorVer}.{Superblock.MinorVer}";

        public Dictionary<string, byte[]> GetRootXattrs() => ParseXattrs(ReadNode(Superblock.RootIno));

        // ---- low level ----

        private byte[] ReadBlockRaw(uint BlkAddr)
        {
            long Off = (long)BlkAddr * BlockSize;
            if ((BlkAddr == 0) || (Off + BlockSize > Raw.Length))
                return null;

            var Res = new byte[BlockSize];
            Array.Copy(Raw, Off, Res, 0, BlockSize);
            return Res;
        }

        public uint ReadNat(uint Nid)
        {
            if (Checkpoint.TryJournal(Nid, out uint JBlk))
                return JBlk;

            const int PerBlock = 455;
            long Blk = Checkpoint.NatAddr(Nid);
            long Off = Blk * BlockSize + (Nid % PerBlock) * 9;
            if (Off + 9 > Raw.Length)
                return 0;
            return Raw.ReadUInt32(Off + 5);
        }

        private byte[] ReadNode(uint Nid)
        {
            uint Blk = ReadNat(Nid);
            if ((Blk < Superblock.MainBlkaddr) || (Blk >= Superblock.BlockCount))
                return null;
            return ReadBlockRaw(Blk);
        }

        private static uint U16(byte[] D, int O) => BitConverter.ToUInt16(D, O);
        private static uint U32(byte[] D, int O) => BitConverter.ToUInt32(D, O);
        private static ulong U64(byte[] D, int O) => BitConverter.ToUInt64(D, O);

        private static FilesystemItemType TypeOf(byte Ft) => Ft switch
        {
            1 => FilesystemItemType.File,
            2 => FilesystemItemType.Directory,
            3 => FilesystemItemType.Character,
            4 => FilesystemItemType.Block,
            5 => FilesystemItemType.Fifo,
            6 => FilesystemItemType.Socket,
            7 => FilesystemItemType.SymLink,
            _ => FilesystemItemType.Unknown
        };

        private static uint PermOf(FilesystemItemType T) => (T == FilesystemItemType.Directory) ? 0x1EDu : 0x1A4u;

        private class DEntry
        {
            public string Name;
            public uint Ino;
            public FilesystemItemType Type;
        }

        private List<DEntry> ParseDentries(byte[] Data, int BitmapBase, int EntryBase, int NameBase, int Count)
        {
            var Res = new List<DEntry>();
            for (int i = 0; i < Count; i++)
            {
                if (((Data[BitmapBase + i / 8] >> (i % 8)) & 1) == 0)
                    continue;

                int Off = EntryBase + i * DENTRY_SIZE;
                if (Off + DENTRY_SIZE > Data.Length)
                    break;

                uint Ino = U32(Data, Off + 4);
                int NameLen = (int)U16(Data, Off + 8);
                byte Ft = Data[Off + 10];
                if ((NameLen <= 0) || (NameLen > 255))
                    continue;

                int NameOff = NameBase + i * SLOT_LEN;
                if (NameOff + NameLen > Data.Length)
                    continue;

                var Name = Encoding.UTF8.GetString(Data, NameOff, NameLen);
                if ((Name == ".") || (Name == ".."))
                    continue;

                Res.Add(new DEntry { Name = Name, Ino = Ino, Type = TypeOf(Ft) });
            }

            return Res;
        }

        private List<DEntry> ReadDentries(uint Inum)
        {
            var Node = ReadNode(Inum);
            if (Node == null)
                return null;

            uint Mode = U16(Node, INO_MODE);
            if ((Mode & 0xF000) != 0x4000)
                return null;

            byte Inline = Node[INO_INLINE];
            if ((Inline & INLINE_DENTRY) != 0)
            {
                // inline dentry lives at i_addr+4, normal dentry layout but with NR_INLINE_DENTRY slots
                int BitmapBase = INO_ADDR + 4;
                int EntryBase = BitmapBase + DENTRY_BASE;
                int NameBase = EntryBase + NR_INLINE_DENTRY * DENTRY_SIZE;
                return ParseDentries(Node, BitmapBase, EntryBase, NameBase, NR_INLINE_DENTRY);
            }

            uint Addr0 = U32(Node, INO_ADDR);
            if (Addr0 < Superblock.MainBlkaddr)
                return new List<DEntry>();

            var Res = new List<DEntry>();
            long DirSize = Convert.ToInt64(U64(Node, INO_SIZE));
            int Blocks = (int)Math.Max(1, (DirSize + BlockSize - 1) / BlockSize);
            int Seen = 0;

            foreach (var Blk in InodeDataBlocks(Node))
            {
                if (Seen++ >= Blocks)
                    break;
                if (Blk < Superblock.MainBlkaddr)
                    continue;

                var Data = ReadBlockRaw(Blk);
                if (Data == null)
                    continue;

                Res.AddRange(ParseDentries(Data, 0, DENTRY_BASE, DENTRY_BASE + NR_DENTRY * DENTRY_SIZE, NR_DENTRY));
            }

            return Res;
        }

        private bool Resolve(string Path, out uint Inum)
        {
            Inum = Superblock.RootIno;
            if (string.IsNullOrEmpty(Path) || (Path == "/") || (Path == "."))
                return true;

            foreach (var Part in Path.Trim('/').Split('/'))
            {
                if (Part.Length == 0)
                    continue;

                var Entries = ReadDentries(Inum);
                if (Entries == null)
                    return false;

                bool Found = false;
                foreach (var E in Entries)
                    if (string.Equals(E.Name, Part, StringComparison.Ordinal))
                    {
                        Inum = E.Ino;
                        Found = true;
                        break;
                    }

                if (!Found)
                    return false;
            }

            return true;
        }

        // ---- IFilesystemReader ----

        public FilesystemEntry[] ReadDir(string Path)
        {
            if (!Resolve(Path, out uint Inum))
                return null;

            var Entries = ReadDentries(Inum);
            if (Entries == null)
                return null;

            var Res = new List<FilesystemEntry>();
            foreach (var E in Entries)
            {
                uint Size = 0;
                uint Perm = PermOf(E.Type);

                var Child = ReadNode(E.Ino);
                if (Child != null)
                {
                    Size = Convert.ToUInt32(U64(Child, INO_SIZE));
                    if (E.Type == FilesystemItemType.Directory)
                        Size = 0;
                }

                var Entry = new FilesystemEntry(E.Type, Universal.Helper.FsHelper.CombinePath(Path, E.Name), 0, 0, Perm, Size);
                var Xattrs = ParseXattrs(Child);
                if (Xattrs != null)
                    Entry.Xattrs = Xattrs;

                Res.Add(Entry);
            }

            // skip unsupported entries
            Res.RemoveAll(E => E.NodeType == FilesystemItemType.Unknown);
            return Res.ToArray();
        }

        private static string XattrPrefix(byte Index)
        {
            switch (Index)
            {
                case 1: return "user.";
                case 2: return "system.posix_acl_access";
                case 3: return "system.posix_acl_default";
                case 4: return "trusted.";
                case 5: return "lustre.";
                case 6: return "security.";
                case 7: return "system.advise";
                case 9: return "encryption.";
                case 11: return "verity.";
                default: return null;
            }
        }

        private Dictionary<string, byte[]> ParseXattrs(byte[] Node)
        {
            if (Node == null)
                return null;

            Dictionary<string, byte[]> Res = null;

            if ((Node[INO_INLINE] & INLINE_XATTR) != 0)
            {
                int Off = INO_ADDR + (ADDRS_PER_INODE - INLINE_XATTR_ADDRS) * 4;
                ParseXattrBlock(Node, Off, INLINE_XATTR_ADDRS * 4, ref Res);
            }

            uint Xnid = U32(Node, INO_XATTR_NID);
            if (Xnid != 0)
            {
                var Blk = ReadNode(Xnid);
                if (Blk != null)
                    ParseXattrBlock(Blk, 0, BlockSize - 24, ref Res);
            }

            return Res;
        }

        private static void ParseXattrBlock(byte[] B, int Off, int Max, ref Dictionary<string, byte[]> Res)
        {
            if ((Off + 4 > B.Length) || (U32(B, Off) != XATTR_MAGIC))
                return;

            int P = Off + 24;
            int End = Math.Min(B.Length, Off + Max) - 4;

            while ((P + 4 <= B.Length) && (P <= End))
            {
                if (U32(B, P) == 0)
                    break;

                byte Index = B[P];
                int NameLen = B[P + 1];
                int ValueSize = (int)U16(B, P + 2);
                int NamePos = P + 4;
                int ValuePos = NamePos + NameLen;

                var Prefix = XattrPrefix(Index);
                if ((Prefix != null) && (ValuePos + ValueSize <= B.Length))
                {
                    var Name = Prefix + Encoding.UTF8.GetString(B, NamePos, NameLen);
                    var Value = new byte[ValueSize];
                    Array.Copy(B, ValuePos, Value, 0, ValueSize);
                    (Res ?? (Res = new Dictionary<string, byte[]>()))[Name] = Value;
                }

                int EntrySize = 4 + NameLen + ValueSize;
                P += (EntrySize + 3) & ~3;
            }
        }

        public byte[] Read(string Path)
        {
            if (!Resolve(Path, out uint Inum))
                return null;

            var Node = ReadNode(Inum);
            if (Node == null)
                return null;
            if ((U16(Node, INO_MODE) & 0xF000) != 0x8000)
                return null;

            if ((U32(Node, INO_FLAGS) & F2FS_COMPR_FL) != 0)
                return ReadCompressed(Node, Convert.ToInt64(U64(Node, INO_SIZE)));

            long Size = Convert.ToInt64(U64(Node, INO_SIZE));
            if (Size <= 0)
                return new byte[0];

            byte Inline = Node[INO_INLINE];
            if ((Inline & INLINE_DATA) != 0)
            {
                int DataOff = INO_ADDR + 4;
                int Len = (int)Math.Min(Size, Node.Length - DataOff);
                var Inl = new byte[Len];
                Array.Copy(Node, DataOff, Inl, 0, Len);
                return Inl;
            }

            var Res = new byte[Size];
            long Done = 0;
            foreach (var Blk in InodeDataBlocks(Node))
            {
                if (Done >= Size)
                    break;

                int Len = (int)Math.Min(BlockSize, Size - Done);
                if (Blk < Superblock.MainBlkaddr)
                {
                    Done += Len;              // hole: leave zeros
                    continue;
                }

                var Data = ReadBlockRaw(Blk);
                if (Data != null)
                    Array.Copy(Data, 0, Res, Done, Len);
                Done += Len;
            }

            return Res;
        }

        /// <summary>Logical data-block addresses of an inode (direct, 2 direct nodes, 2 indirect, double indirect).</summary>
        private IEnumerable<uint> InodeDataBlocks(byte[] Node)
        {
            int Xattr = ((Node[INO_INLINE] & INLINE_XATTR) != 0) ? 50 : 0;   // DEFAULT_INLINE_XATTR_ADDRS
            int D = (INO_NID - INO_ADDR) / 4 - Xattr;
            int B = (BlockSize - 24) / 4;

            for (int i = 0; i < D; i++)
                yield return U32(Node, INO_ADDR + i * 4);

            // i_nid[0], i_nid[1] - direct nodes
            for (int k = 0; k < 2; k++)
            {
                var DN = ReadNode(U32(Node, INO_NID + k * 4));
                if (DN == null)
                {
                    for (int i = 0; i < B; i++)
                        yield return 0;
                }
                else
                {
                    for (int i = 0; i < B; i++)
                        yield return U32(DN, i * 4);
                }
            }

            // i_nid[2], i_nid[3] - indirect nodes
            for (int k = 0; k < 2; k++)
            {
                var IND = ReadNode(U32(Node, INO_NID + (2 + k) * 4));
                for (int j = 0; j < B; j++)
                {
                    var DB = (IND == null) ? null : ReadNode(U32(IND, j * 4));
                    for (int i = 0; i < B; i++)
                        yield return (DB == null) ? 0u : U32(DB, i * 4);
                }
            }

            // i_nid[4] - double indirect node
            var DD = ReadNode(U32(Node, INO_NID + 16));
            for (int m = 0; m < B; m++)
            {
                var IND = (DD == null) ? null : ReadNode(U32(DD, m * 4));
                for (int j = 0; j < B; j++)
                {
                    var DB = (IND == null) ? null : ReadNode(U32(IND, j * 4));
                    for (int i = 0; i < B; i++)
                        yield return (DB == null) ? 0u : U32(DB, i * 4);
                }
            }
        }

        private byte[] ReadCompressed(byte[] Node, long Size)
        {
            if (Size <= 0)
                return new byte[0];

            int C = 1 << Node[INO_COMPR_BASE + 33];
            int Extra = (int)U16(Node, INO_COMPR_BASE) / 4;
            int Xattr = ((Node[INO_INLINE] & INLINE_XATTR) != 0) ? INLINE_XATTR_ADDRS : 0;
            int Bc0 = (BlockSize - 24) / 4;
            int Dc = (ADDRS_PER_INODE - Extra - Xattr) / C * C;
            int Bc = Bc0 / C * C;

            var N1 = (U32(Node, INO_NID + 0) != 0) ? ReadNode(U32(Node, INO_NID + 0)) : null;
            var N2 = (U32(Node, INO_NID + 4) != 0) ? ReadNode(U32(Node, INO_NID + 4)) : null;
            var I1 = (U32(Node, INO_NID + 8) != 0) ? ReadNode(U32(Node, INO_NID + 8)) : null;
            var I2 = (U32(Node, INO_NID + 12) != 0) ? ReadNode(U32(Node, INO_NID + 12)) : null;
            var DD = (U32(Node, INO_NID + 16) != 0) ? ReadNode(U32(Node, INO_NID + 16)) : null;

            long IND = (long)Bc * Bc;

            uint GetAddr(long L)
            {
                if (L < Dc) return U32(Node, INO_ADDR + (int)(Extra + L) * 4);
                L -= Dc;
                if (L < Bc) return (N1 == null) ? 0u : U32(N1, (int)L * 4);
                L -= Bc;
                if (L < Bc) return (N2 == null) ? 0u : U32(N2, (int)L * 4);
                L -= Bc;

                if (L < IND)
                {
                    var Dn = (I1 == null) ? null : ReadNode(U32(I1, (int)(L / Bc) * 4));
                    return (Dn == null) ? 0u : U32(Dn, (int)(L % Bc) * 4);
                }
                L -= IND;

                if (L < IND)
                {
                    var Dn = (I2 == null) ? null : ReadNode(U32(I2, (int)(L / Bc) * 4));
                    return (Dn == null) ? 0u : U32(Dn, (int)(L % Bc) * 4);
                }
                L -= IND;

                {
                    var Ind = (DD == null) ? null : ReadNode(U32(DD, (int)(L / IND) * 4));
                    long rr = L % IND;
                    var Dn = (Ind == null) ? null : ReadNode(U32(Ind, (int)(rr / Bc) * 4));
                    return (Dn == null) ? 0u : U32(Dn, (int)(rr % Bc) * 4);
                }
            }

            int Count = (int)((Size + BlockSize - 1) / BlockSize);
            int Clusters = (Count + C - 1) / C;
            var Res = new byte[Size];
            long Done = 0;

            for (int ci = 0; (ci < Clusters) && (Done < Size); ci++)
            {
                long Base = (long)ci * C;
                uint A0 = GetAddr(Base);

                if (A0 == COMPRESS_ADDR)
                {
                    var First = ReadBlockRaw(GetAddr(Base + 1));
                    int Clen = (First == null) ? 0 : (int)U32(First, 0);
                    int N = (24 + Clen + BlockSize - 1) / BlockSize;

                    var Payload = new byte[N * BlockSize];
                    for (int i = 0; i < N; i++)
                    {
                        var B = ReadBlockRaw(GetAddr(Base + 1 + i));
                        if (B != null)
                            Array.Copy(B, 0, Payload, i * BlockSize, BlockSize);
                    }

                    var Comp = new byte[Clen];
                    Array.Copy(Payload, 24, Comp, 0, Clen);
                    var Dec = DecompressCluster(Node[INO_COMPR_BASE + 32], Comp, C * BlockSize);
                    int Len = (int)Math.Min(C * BlockSize, Size - Done);
                    Array.Copy(Dec, 0, Res, Done, Len);
                    Done += Len;
                }
                else
                {
                    for (int j = 0; (j < C) && (Done < Size); j++)
                    {
                        var B = ReadBlockRaw(GetAddr(Base + j));
                        int Len = (int)Math.Min(BlockSize, Size - Done);
                        if (B != null)
                            Array.Copy(B, 0, Res, Done, Len);
                        Done += Len;
                    }
                }
            }

            return Res;
        }

        private static byte[] DecompressCluster(byte Algo, byte[] Comp, int Expected)
        {
            switch (Algo)
            {
                case 2:
                    return NyaFs.ImageFormat.Compressors.ZStd.Decompress(Comp);
                case 1:
                    return NyaFs.ImageFormat.Compressors.Lz4Raw.Decompress(Comp, Expected);
                case 0:
                case 3:
                    {
                        var Raw = NyaLZO.LZO1xDecompressor.Decompress(Comp);
                        if (Raw == null)
                            return new byte[Expected];
                        if (Raw.Length >= Expected)
                            return Raw;
                        var Res = new byte[Expected];
                        Array.Copy(Raw, Res, Raw.Length);
                        return Res;
                    }
                default:
                    throw new NotSupportedException($"F2FS compression algorithm {Algo} is not supported");
            }
        }

        public string ReadLink(string Path)
        {
            if (!Resolve(Path, out uint Inum))
                return null;

            var Node = ReadNode(Inum);
            if (Node == null)
                return null;
            if ((U16(Node, INO_MODE) & 0xF000) != 0xA000)
                return null;

            long Size = Convert.ToInt64(U64(Node, INO_SIZE));
            byte Inline = Node[INO_INLINE];

            if ((Inline & INLINE_DATA) != 0)
            {
                int DataOff = INO_ADDR + 4;
                int Len = (int)Math.Min(Size, Node.Length - DataOff);
                return Encoding.UTF8.GetString(Node, DataOff, Len);
            }

            var Data = Read(Path);
            return (Data == null) ? null : Encoding.UTF8.GetString(Data);
        }

        public DeviceInfo ReadDevice(string Path) => null;
    }
}
