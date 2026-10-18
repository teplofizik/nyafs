using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.UbiFs.Ubifs
{
    /// <summary>
    /// Serializers for UBIFS on-flash nodes (ubifs-media.h). Every node starts with the
    /// common header (magic, crc, sqnum, len, node_type, group_type, padding) and the node
    /// CRC covers the node from offset 8 to the end.
    /// </summary>
    public static class UbifsNodeWriter
    {
        public const uint MAGIC = 0x06101831;
        public const int HeaderSize = 24;
        public const int KeySize = 16;   // on-flash key area is UBIFS_MAX_KEY_LEN

        public const int InoNodeSize = 0xA0;      // fixed part before inode data
        public const int DentNodeSize = 0x38;     // fixed part before name
        public const int DataNodeSize = 0x30;     // fixed part before data
        public const int SbNodeSize = 0x1000;     // reserved size of the superblock node
        public const int MstNodeSize = 0x200;     // reserved size of the master node

        private static void WriteLE16(byte[] D, int O, ushort V) { D[O] = (byte)V; D[O + 1] = (byte)(V >> 8); }
        private static void WriteLE32(byte[] D, int O, uint V) { D[O] = (byte)V; D[O + 1] = (byte)(V >> 8); D[O + 2] = (byte)(V >> 16); D[O + 3] = (byte)(V >> 24); }
        private static void WriteLE64(byte[] D, int O, ulong V) { for (int i = 0; i < 8; i++) D[O + i] = (byte)(V >> (i * 8)); }

        /// <summary>Write the common header, then the CRC (covers bytes 8..len).</summary>
        public static void WriteCommonHeader(byte[] Node, UbifsNodeType Type, ulong Sqnum, uint Len)
        {
            WriteLE32(Node, 0x00, MAGIC);
            WriteLE32(Node, 0x04, 0);
            WriteLE64(Node, 0x08, Sqnum);
            WriteLE32(Node, 0x10, Len);
            Node[0x14] = (byte)Type;
            Node[0x15] = 0;
            Node[0x16] = 0;
            Node[0x17] = 0;

            var CrcData = new byte[Len - 8];
            Array.Copy(Node, 8, CrcData, 0, (int)(Len - 8));
            WriteLE32(Node, 0x04, UbiFs.Crc32.CalcCrc(CrcData));
        }

        /// <summary>Write the 8-byte simple key (16-byte on-flash area) at the node's key offset.</summary>
        public static void WriteKey(byte[] Node, int Offset, uint Inum, UbifsKeyType Type, uint Val)
        {
            WriteLE32(Node, Offset, Inum);
            WriteLE32(Node, Offset + 4, ((uint)Type << 29) | (Val & 0x1FFFFFFF));
            for (int i = 8; i < KeySize; i++)
                Node[Offset + i] = 0;
        }

        /// <summary>Write the 8-byte simple key at the given offset (index branches).</summary>
        public static void WriteKey8(byte[] Node, int Offset, uint Inum, UbifsKeyType Type, uint Val)
        {
            WriteLE32(Node, Offset, Inum);
            WriteLE32(Node, Offset + 4, ((uint)Type << 29) | (Val & 0x1FFFFFFF));
        }

        // ---- node builders ----

        public static byte[] Inode(uint Inum, uint Mode, ulong Size, uint Uid, uint Gid, uint Nlink,
                                   ulong Sqnum, ulong MTimeSec, byte[] InlineData, ushort ComprType = 0)
        {
            int DataLen = InlineData?.Length ?? 0;
            int Len = InoNodeSize + DataLen;
            var N = new byte[Len];

            WriteCommonHeader(N, UbifsNodeType.Ino, Sqnum, (uint)Len);
            WriteKey(N, 0x18, Inum, UbifsKeyType.Ino, 0);
            WriteLE64(N, 0x28, 0);                    // creat_sqnum
            WriteLE64(N, 0x30, Size);
            WriteLE64(N, 0x38, MTimeSec);             // atime
            WriteLE64(N, 0x40, MTimeSec);             // ctime
            WriteLE64(N, 0x48, MTimeSec);             // mtime
            WriteLE32(N, 0x50, 0);                    // atime_nsec
            WriteLE32(N, 0x54, 0);                    // ctime_nsec
            WriteLE32(N, 0x58, 0);                    // mtime_nsec
            WriteLE32(N, 0x5C, Nlink);
            WriteLE32(N, 0x60, Uid);
            WriteLE32(N, 0x64, Gid);
            WriteLE32(N, 0x68, Mode);
            WriteLE32(N, 0x6C, 0);                    // flags
            WriteLE32(N, 0x70, (uint)DataLen);        // data_len
            WriteLE32(N, 0x74, 0);                    // xattr_cnt
            WriteLE32(N, 0x78, 0);                    // xattr_size
            WriteLE32(N, 0x80, 0);                    // xattr_names
            WriteLE16(N, 0x84, ComprType);
            if (DataLen > 0)
                Array.Copy(InlineData, 0, N, InoNodeSize, DataLen);

            // recompute CRC now that the body is filled
            WriteCommonHeader(N, UbifsNodeType.Ino, Sqnum, (uint)Len);
            return N;
        }

        public static byte[] Dent(uint ParentInum, ulong TargetInum, byte Type, uint Hash, string Name, ulong Sqnum)
        {
            var NameBytes = Encoding.UTF8.GetBytes(Name);
            int Len = DentNodeSize + NameBytes.Length + 1;   // name is NUL-terminated
            var N = new byte[Len];

            WriteKey(N, 0x18, ParentInum, UbifsKeyType.Dent, Hash);
            WriteLE64(N, 0x28, TargetInum);
            N[0x30] = 0;                              // padding1
            N[0x31] = Type;
            WriteLE16(N, 0x32, (ushort)NameBytes.Length);
            WriteLE32(N, 0x34, 0);                    // cookie
            Array.Copy(NameBytes, 0, N, DentNodeSize, NameBytes.Length);

            WriteCommonHeader(N, UbifsNodeType.Dent, Sqnum, (uint)Len);
            return N;
        }

        public static byte[] Data(uint Inum, uint Block, byte[] Stored, uint UncompressedSize, ushort ComprType, ulong Sqnum)
        {
            int Len = DataNodeSize + Stored.Length;
            var N = new byte[Len];

            WriteKey(N, 0x18, Inum, UbifsKeyType.Data, Block);
            WriteLE32(N, 0x28, UncompressedSize);      // size (uncompressed)
            WriteLE16(N, 0x2C, ComprType);
            WriteLE16(N, 0x2E, (ushort)Stored.Length); // compr_size (== size when not compressed)
            Array.Copy(Stored, 0, N, DataNodeSize, Stored.Length);

            WriteCommonHeader(N, UbifsNodeType.Data, Sqnum, (uint)Len);
            return N;
        }

        /// <summary>R5 hash (borrowed from reiserfs), used for directory entry keys.</summary>
        public static uint R5Hash(string Name)
        {
            uint a = 0;
            foreach (char Ch in Name)
            {
                sbyte s = (sbyte)Ch;
                a += (uint)(s << 4);
                a += (uint)(s >> 4);
                a *= 11;
            }
            a &= 0x1FFFFFFF;
            if (a <= 2)
                a += 3;
            return a;
        }

        /// <summary>Padding node: ch + pad_len (u32) = 28 bytes.</summary>
        public static byte[] Pad(uint PadLen, ulong Sqnum)
        {
            var N = new byte[28];
            WriteLE32(N, 0x18, PadLen);
            WriteCommonHeader(N, UbifsNodeType.Pad, Sqnum, 28);
            return N;
        }

        /// <summary>Commit start node: ch + cmt_no (u64) = 32 bytes (written at the log head).</summary>
        public static byte[] Cs(ulong Sqnum, ulong CmtNo)
        {
            var N = new byte[32];
            WriteLE64(N, 0x18, CmtNo);
            WriteCommonHeader(N, UbifsNodeType.Cs, Sqnum, 32);
            return N;
        }

        // ---- superblock / master ----

        public static byte[] Superblock(uint MinIoSize, uint LebSize, uint LebCnt, uint MaxLebCnt,
                                        ulong MaxBudBytes, uint LogLebs, uint LptLebs, uint OrphLebs,
                                        uint JheadCnt, uint Fanout, uint LsaveCnt, ushort DefaultCompr,
                                        uint Flags, byte[] Uuid, ulong Sqnum)
        {
            var N = new byte[SbNodeSize];

            N[0x1A] = 0;                              // key_hash = R5
            N[0x1B] = 0;                              // key_fmt = simple
            WriteLE32(N, 0x1C, Flags);               // sup_flags
            WriteLE32(N, 0x20, MinIoSize);
            WriteLE32(N, 0x24, LebSize);
            WriteLE32(N, 0x28, LebCnt);
            WriteLE32(N, 0x2C, MaxLebCnt);
            WriteLE64(N, 0x30, MaxBudBytes);
            WriteLE32(N, 0x38, LogLebs);
            WriteLE32(N, 0x3C, LptLebs);
            WriteLE32(N, 0x40, OrphLebs);
            WriteLE32(N, 0x44, JheadCnt);
            WriteLE32(N, 0x48, Fanout);
            WriteLE32(N, 0x4C, LsaveCnt);
            WriteLE32(N, 0x50, 4);                    // fmt_version
            WriteLE16(N, 0x54, DefaultCompr);
            WriteLE32(N, 0x58, 0);                    // rp_uid
            WriteLE32(N, 0x5C, 0);                    // rp_gid
            WriteLE64(N, 0x60, 0);                    // rp_size
            WriteLE32(N, 0x68, 1000000000);           // time_gran
            if (Uuid != null)
                Array.Copy(Uuid, 0, N, 0x6C, 16);
            WriteLE32(N, 0x7C, 0);                    // ro_compat_version

            WriteCommonHeader(N, UbifsNodeType.Sb, Sqnum, SbNodeSize);
            return N;
        }

        public static byte[] Master(ulong HighestInum, ulong CmtNo, uint Flags, uint LogLnum,
                                    uint RootLnum, uint RootOffs, uint RootLen, uint GcLnum,
                                    uint IheadLnum, uint IheadOffs, ulong IndexSize,
                                    ulong TotalFree, ulong TotalDirty, ulong TotalUsed, ulong TotalDead, ulong TotalDark,
                                    uint LptLnum, uint LptOffs, uint NheadLnum, uint NheadOffs,
                                    uint LtabLnum, uint LtabOffs, uint LsaveLnum, uint LsaveOffs,
                                    uint LscanLnum, uint EmptyLebs, uint IdxLebs, uint LebCnt, ulong Sqnum)
        {
            var N = new byte[MstNodeSize];

            WriteLE64(N, 0x18, HighestInum);
            WriteLE64(N, 0x20, CmtNo);
            WriteLE32(N, 0x28, Flags);
            WriteLE32(N, 0x2C, LogLnum);
            WriteLE32(N, 0x30, RootLnum);
            WriteLE32(N, 0x34, RootOffs);
            WriteLE32(N, 0x38, RootLen);
            WriteLE32(N, 0x3C, GcLnum);
            WriteLE32(N, 0x40, IheadLnum);
            WriteLE32(N, 0x44, IheadOffs);
            WriteLE64(N, 0x48, IndexSize);
            WriteLE64(N, 0x50, TotalFree);
            WriteLE64(N, 0x58, TotalDirty);
            WriteLE64(N, 0x60, TotalUsed);
            WriteLE64(N, 0x68, TotalDead);
            WriteLE64(N, 0x70, TotalDark);
            WriteLE32(N, 0x78, LptLnum);
            WriteLE32(N, 0x7C, LptOffs);
            WriteLE32(N, 0x80, NheadLnum);
            WriteLE32(N, 0x84, NheadOffs);
            WriteLE32(N, 0x88, LtabLnum);
            WriteLE32(N, 0x8C, LtabOffs);
            WriteLE32(N, 0x90, LsaveLnum);
            WriteLE32(N, 0x94, LsaveOffs);
            WriteLE32(N, 0x98, LscanLnum);
            WriteLE32(N, 0x9C, EmptyLebs);
            WriteLE32(N, 0xA0, IdxLebs);
            WriteLE32(N, 0xA4, LebCnt);

            WriteCommonHeader(N, UbifsNodeType.Mst, Sqnum, MstNodeSize);
            return N;
        }

        /// <summary>Indexing node: ch + child_cnt + level + branches (lnum/offs/len/key).</summary>
        public static byte[] Index(int Level, List<(uint Lnum, uint Offs, uint Len, uint KeyInum, UbifsKeyType KeyType, uint KeyVal)> Branches, ulong Sqnum)
        {
            int Len = 0x1C + Branches.Count * 20;
            var N = new byte[Len];

            WriteLE16(N, 0x18, (ushort)Branches.Count);
            WriteLE16(N, 0x1A, (ushort)Level);

            for (int i = 0; i < Branches.Count; i++)
            {
                int Br = 0x1C + i * 20;
                var B = Branches[i];
                WriteLE32(N, Br + 0, B.Lnum);
                WriteLE32(N, Br + 4, B.Offs);
                WriteLE32(N, Br + 8, B.Len);
                WriteKey8(N, Br + 12, B.KeyInum, B.KeyType, B.KeyVal);
            }

            WriteCommonHeader(N, UbifsNodeType.Idx, Sqnum, (uint)Len);
            return N;
        }
    }
}
