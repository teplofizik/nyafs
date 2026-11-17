using System;
using System.Collections.Generic;
using NyaIO.Data;

namespace NyaFs.Filesystem.F2fs
{
    /// <summary>
    /// F2FS checkpoint (f2fs_checkpoint): selects the newest valid pack, exposes the NAT
    /// version bitmap and the NAT journal (from the hot-data summary block) so NAT lookups
    /// return the current node block address.
    /// </summary>
    public class F2fsCheckpoint
    {
        public const uint F2FS_MAGIC = 0xF2F52010;
        public const uint CP_UMOUNT = 0x1;
        public const uint CP_COMPACT_SUM = 0x4;
        public const uint CP_NAT_BITS = 0x80;
        public const uint CP_LARGE_NAT_BITMAP = 0x400;

        private const int SUM_JOURNAL_SIZE = 507;
        private const int SUM_ENTRIES_SIZE = 3584;
        private const int NR_CURSEG_TYPE = 6;
        private const int NR_CURSEG_DATA_TYPE = 3;

        public uint BlockAddr;
        public ulong Version;
        public uint Flags;
        public uint PackTotal;
        public uint PackStartSum;
        public uint SitBitmapBytes;
        public uint NatBitmapBytes;
        public byte[] NatBitmap = new byte[0];

        public readonly Dictionary<uint, uint> NatJournal = new Dictionary<uint, uint>();   // nid -> block_addr

        private byte[] Raw;
        private uint NatBlkaddr;
        private int BlocksPerSeg;
        private int LogBlocksPerSeg;

        public static F2fsCheckpoint Read(F2fsSuperblock Sb, byte[] Raw)
        {
            var Cp1 = TryRead(Sb, Raw, Sb.CpBlkaddr);
            var Cp2 = TryRead(Sb, Raw, Sb.CpBlkaddr + (uint)Sb.BlocksPerSeg);

            F2fsCheckpoint Chosen = null;
            if ((Cp1 != null) && (Cp2 != null))
                Chosen = ((long)(Cp2.Version - Cp1.Version) > 0) ? Cp2 : Cp1;
            else
                Chosen = Cp1 ?? Cp2;

            if (Chosen == null)
                throw new InvalidOperationException("F2FS: no valid checkpoint found");

            Chosen.LoadBitmaps(Sb);
            Chosen.LoadJournal(Sb);
            return Chosen;
        }

        private static F2fsCheckpoint TryRead(F2fsSuperblock Sb, byte[] Raw, uint BlkAddr)
        {
            long Off = (long)BlkAddr * Sb.BlockSize;
            if (Off + Sb.BlockSize > Raw.Length)
                return null;

            var Cp = new F2fsCheckpoint
            {
                Raw = Raw,
                BlockAddr = BlkAddr,
                NatBlkaddr = Sb.NatBlkaddr,
                BlocksPerSeg = Sb.BlocksPerSeg,
                LogBlocksPerSeg = (int)Sb.LogBlocksPerSeg
            };

            Cp.Version = Raw.ReadUInt64(Off + 0x00);
            Cp.Flags = Raw.ReadUInt32(Off + 0x84);
            Cp.PackTotal = Raw.ReadUInt32(Off + 0x88);
            Cp.PackStartSum = Raw.ReadUInt32(Off + 0x8C);
            Cp.SitBitmapBytes = Raw.ReadUInt32(Off + 0x9C);
            Cp.NatBitmapBytes = Raw.ReadUInt32(Off + 0xA0);
            uint ChecksumOffset = Raw.ReadUInt32(Off + 0xA4);

            if ((ChecksumOffset < 192) || (ChecksumOffset > (Sb.BlockSize - 4)))
                return null;

            uint Stored = Raw.ReadUInt32(Off + ChecksumOffset);
            uint Calc = Crc32(F2FS_MAGIC, Raw, Off, ChecksumOffset);
            if (ChecksumOffset < (Sb.BlockSize - 4))
                Calc = Crc32(Calc, Raw, Off + ChecksumOffset + 4, (uint)(Sb.BlockSize - (ChecksumOffset + 4)));

            if (Stored != Calc)
                return null;

            return Cp;
        }

        private static uint Crc32(uint Crc, byte[] Data, long Off, uint Len)
        {
            for (uint i = 0; i < Len; i++)
            {
                Crc ^= Data[Off + i];
                for (int b = 0; b < 8; b++)
                    Crc = ((Crc & 1) != 0) ? ((Crc >> 1) ^ 0xEDB88320u) : (Crc >> 1);
            }
            return Crc;
        }

        private void LoadBitmaps(F2fsSuperblock Sb)
        {
            long Base = (long)BlockAddr * Sb.BlockSize;
            long Ptr;

            if ((Flags & CP_LARGE_NAT_BITMAP) != 0)
                Ptr = Base + 192 + 4;                       // SIT large checksum = 4 bytes
            else if (Sb.CpPayload > 0)
                Ptr = Base + 192;                           // NAT bitmap in header block
            else
                Ptr = Base + 192 + SitBitmapBytes;          // SIT first, then NAT

            NatBitmap = new byte[NatBitmapBytes];
            long Avail = Math.Min(NatBitmapBytes, Raw.Length - Ptr);
            if (Avail > 0)
                Array.Copy(Raw, Ptr, NatBitmap, 0, (int)Avail);
        }

        private void LoadJournal(F2fsSuperblock Sb)
        {
            long Off;
            int EntryBase;

            if ((Flags & CP_COMPACT_SUM) != 0)
            {
                // compacted summaries: NAT journal is the first SUM_JOURNAL_SIZE bytes
                uint Jb = BlockAddr + PackStartSum;
                Off = (long)Jb * Sb.BlockSize;
                EntryBase = 2;
            }
            else
            {
                int Base = ((Flags & CP_UMOUNT) != 0) ? NR_CURSEG_TYPE : NR_CURSEG_DATA_TYPE;
                uint Jb = (uint)(BlockAddr + PackTotal - (Base + 1));
                Off = (long)Jb * Sb.BlockSize + SUM_ENTRIES_SIZE;
                EntryBase = 2;
            }

            if (Off + 2 > Raw.Length)
                return;

            int Count = Raw.ReadUInt16(Off + 0);
            for (int i = 0; i < Count; i++)
            {
                long E = Off + EntryBase + i * 13;
                if (E + 13 > Raw.Length)
                    break;

                uint Nid = Raw.ReadUInt32(E + 0);
                uint Blk = Raw.ReadUInt32(E + 9);
                NatJournal[Nid] = Blk;
            }
        }

        /// <summary>Current NAT block address for a node id (nat_bitmap selects the set).</summary>
        public uint NatAddr(uint Nid)
        {
            int PerBlock = 455;
            uint BlockOff = Nid / (uint)PerBlock;
            uint Addr = (uint)(NatBlkaddr + (BlockOff << 1) - (BlockOff & (uint)(BlocksPerSeg - 1)));
            if (TestBit((int)BlockOff, NatBitmap))
                Addr += (uint)BlocksPerSeg;
            return Addr;
        }

        public bool TryJournal(uint Nid, out uint Blk) => NatJournal.TryGetValue(Nid, out Blk);

        private static bool TestBit(int Nr, byte[] Addr)
        {
            int Byte = Nr >> 3;
            if ((Byte < 0) || (Byte >= Addr.Length))
                return false;
            return (Addr[Byte] & (1 << (7 - (Nr & 7)))) != 0;
        }
    }
}
