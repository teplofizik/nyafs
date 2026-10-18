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
    public class F2fsFsReader : IFilesystemReader
    {
        public readonly F2fsSuperblock Superblock;

        private readonly byte[] Raw;
        private readonly int BlockSize;

        // f2fs_dentry_block geometry (4K page, f2fs constants)
        private const int NR_DENTRY = 214;
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

        private const byte INLINE_DATA = 0x01;
        private const byte INLINE_DENTRY = 0x02;

        public F2fsFsReader(byte[] Data)
        {
            Raw = Data;
            Superblock = new F2fsSuperblock(Data, 0);
            BlockSize = Superblock.BlockSize;
        }

        public F2fsFsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }

        public string Version => $"{Superblock.MajorVer}.{Superblock.MinorVer}";

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
            int PerBlock = BlockSize / 9;
            long Blk = (long)Superblock.NatBlkaddr + (Nid / (uint)PerBlock);
            long Off = Blk * BlockSize + (Nid % (uint)PerBlock) * 9;
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
                return new List<DEntry>();      // inline dentry not parsed yet

            uint Addr0 = U32(Node, INO_ADDR);
            if (Addr0 < Superblock.MainBlkaddr)
                return new List<DEntry>();

            var Data = ReadBlockRaw(Addr0);
            if (Data == null)
                return new List<DEntry>();

            var Res = new List<DEntry>();
            for (int i = 0; i < NR_DENTRY; i++)
            {
                if (((Data[i / 8] >> (i % 8)) & 1) == 0)
                    continue;

                int Off = DENTRY_BASE + i * DENTRY_SIZE;
                uint Ino = U32(Data, Off + 4);
                int NameLen = (int)U16(Data, Off + 8);
                byte Ft = Data[Off + 10];
                if ((NameLen <= 0) || (NameLen > 255))
                    continue;

                int NameOff = DENTRY_BASE + NR_DENTRY * DENTRY_SIZE + i * SLOT_LEN;
                if (NameOff + NameLen > Data.Length)
                    continue;

                var Name = Encoding.UTF8.GetString(Data, NameOff, NameLen);
                if ((Name == ".") || (Name == ".."))
                    continue;

                Res.Add(new DEntry { Name = Name, Ino = Ino, Type = TypeOf(Ft) });
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

                Res.Add(new FilesystemEntry(E.Type, Universal.Helper.FsHelper.CombinePath(Path, E.Name), 0, 0, Perm, Size));
            }

            // skip unsupported entries
            Res.RemoveAll(E => E.NodeType == FilesystemItemType.Unknown);
            return Res.ToArray();
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
            if ((Node[INO_INLINE] & INLINE_DATA) != 0)
                return null;                    // inline data not parsed yet

            long Size = Convert.ToInt64(U64(Node, INO_SIZE));
            if (Size <= 0)
                return new byte[0];

            var Res = new byte[Size];
            long Done = 0;
            int AddrPerBlock = (BlockSize - 24) / 4;

            for (int i = 0; (i < 923) && (Done < Size); i++)
            {
                uint Blk = U32(Node, INO_ADDR + i * 4);
                if (Blk < Superblock.MainBlkaddr)
                    continue;
                var Data = ReadBlockRaw(Blk);
                if (Data == null)
                    continue;

                int Len = (int)Math.Min(BlockSize, Size - Done);
                Array.Copy(Data, 0, Res, Done, Len);
                Done += Len;
            }

            return Res;
        }

        public string ReadLink(string Path) => null;

        public DeviceInfo ReadDevice(string Path) => null;
    }
}
