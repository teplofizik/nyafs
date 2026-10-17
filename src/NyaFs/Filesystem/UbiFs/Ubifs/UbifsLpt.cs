using System;
using System.Collections.Generic;

namespace NyaFs.Filesystem.UbiFs.Ubifs
{
    /// <summary>
    /// UBIFS LEB Properties Tree (LPT) serialization, ported from fs/ubifs/lpt.c.
    /// Nodes use an LSB-first packed bit stream: [crc16 (16 bits)][type (4 bits)]
    /// [node_num (pcnt_bits, big model only)][body]. CRC is crc16(-1) over bytes 2..len.
    /// </summary>
    public static class UbifsLpt
    {
        public const int FANOUT = 4;
        public const int FANOUT_SHIFT = 2;
        public const int CRC_BITS = 16;
        public const int CRC_BYTES = 2;
        public const int TYPE_BITS = 4;

        public const int PNODE = 0;
        public const int NNODE = 1;
        public const int LTAB = 2;
        public const int LSAVE = 3;

        public const int LPROPS_INDEX = 32;

        // ---- geometry ----

        public class Geometry
        {
            public int LebSize;
            public int LebCnt;         // total LEBs (superblock leb_cnt)
            public int MaxLebCnt;      // superblock max_leb_cnt
            public int MainLebs;
            public int MainFirst;
            public int LptLebs;
            public int LptFirst;
            public bool BigLpt;

            public int SpaceBits;      // fls(leb_size) - 3
            public int LptLnumBits;    // fls(lpt_lebs)
            public int LptOffsBits;    // fls(leb_size - 1)
            public int LptSpcBits;     // fls(leb_size)
            public int PcntBits;       // fls(DIV_ROUND_UP(max_leb_cnt, FANOUT) - 1)
            public int LnumBits;       // fls(max_leb_cnt - 1)

            public int LptHght;
            public int PnodeCnt;
            public int NnodeCnt;

            public int PnodeSz;
            public int NnodeSz;
            public int LtabSz;

            public static Geometry Calc(int LebSize, int LebCnt, int MaxLebCnt, int MainLebs, int MainFirst, int LptLebs, int LptFirst, bool BigLpt)
            {
                var G = new Geometry
                {
                    LebSize = LebSize, LebCnt = LebCnt, MaxLebCnt = MaxLebCnt,
                    MainLebs = MainLebs, MainFirst = MainFirst,
                    LptLebs = LptLebs, LptFirst = LptFirst, BigLpt = BigLpt
                };

                int n = MainLebs + MaxLebCnt - LebCnt;
                int maxPnodeCnt = DivRoundUp(n, FANOUT);

                G.LptHght = 1;
                n = FANOUT;
                while (n < maxPnodeCnt) { G.LptHght++; n <<= FANOUT_SHIFT; }

                G.PnodeCnt = DivRoundUp(MainLebs, FANOUT);
                n = DivRoundUp(G.PnodeCnt, FANOUT);
                G.NnodeCnt = n;
                for (int i = 1; i < G.LptHght; i++) { n = DivRoundUp(n, FANOUT); G.NnodeCnt += n; }

                G.SpaceBits = Fls(LebSize) - 3;
                G.LptLnumBits = Fls(LptLebs);
                G.LptOffsBits = Fls(LebSize - 1);
                G.LptSpcBits = Fls(LebSize);
                G.PcntBits = Fls(DivRoundUp(MaxLebCnt, FANOUT) - 1);
                G.LnumBits = Fls(MaxLebCnt - 1);

                int bits = CRC_BITS + TYPE_BITS + (BigLpt ? G.PcntBits : 0) + (G.SpaceBits * 2 + 1) * FANOUT;
                G.PnodeSz = (bits + 7) / 8;

                bits = CRC_BITS + TYPE_BITS + (BigLpt ? G.PcntBits : 0) + (G.LptLnumBits + G.LptOffsBits) * FANOUT;
                G.NnodeSz = (bits + 7) / 8;

                bits = CRC_BITS + TYPE_BITS + LptLebs * G.LptSpcBits * 2;
                G.LtabSz = (bits + 7) / 8;

                return G;
            }
        }

        public static int DivRoundUp(int a, int b) => (a + b - 1) / b;

        /// <summary>fls(x) = position of the highest set bit (1-based); fls(0) = 0.</summary>
        public static int Fls(int x)
        {
            int r = 0;
            while (x > 0) { r++; x >>= 1; }
            return r;
        }

        // ---- lprops ----

        public struct Lprops
        {
            public int Free;
            public int Dirty;
            public int Flags;
        }

        /// <summary>Pack a pnode (leaf) into a fresh buffer of size g.PnodeSz.</summary>
        public static byte[] PackPnode(Geometry G, int PnodeNum, Lprops[] Lebs)
        {
            var Buf = new byte[G.PnodeSz];
            var W = new BitWriter(Buf, CRC_BYTES);
            W.Put(PNODE, TYPE_BITS);
            if (G.BigLpt) W.Put(PnodeNum, G.PcntBits);
            for (int i = 0; i < FANOUT; i++)
            {
                var L = (i < Lebs.Length) ? Lebs[i] : default;
                W.Put((L.Free >> 3) & ((1 << G.SpaceBits) - 1), G.SpaceBits);
                W.Put((L.Dirty >> 3) & ((1 << G.SpaceBits) - 1), G.SpaceBits);
                W.Put((L.Flags & LPROPS_INDEX) != 0 ? 1 : 0, 1);
            }
            W.Flush();
            return Finish(Buf, G.PnodeSz);
        }

        /// <summary>Pack an nnode (internal). Branch target 0 means "no child".</summary>
        public static byte[] PackNnode(Geometry G, int NnodeNum, (int Lnum, int Offs)[] Branches)
        {
            var Buf = new byte[G.NnodeSz];
            var W = new BitWriter(Buf, CRC_BYTES);
            W.Put(NNODE, TYPE_BITS);
            if (G.BigLpt) W.Put(NnodeNum, G.PcntBits);
            for (int i = 0; i < FANOUT; i++)
            {
                int Lnum = (i < Branches.Length) ? Branches[i].Lnum : 0;
                int Offs = (i < Branches.Length) ? Branches[i].Offs : 0;
                if (Lnum == 0) Lnum = G.LptFirst + G.LptLebs; // lpt_last + 1
                W.Put(Lnum - G.LptFirst, G.LptLnumBits);
                W.Put(Offs, G.LptOffsBits);
            }
            W.Flush();
            return Finish(Buf, G.NnodeSz);
        }

        /// <summary>Pack the LPT's own lprops table (one entry per LPT LEB).</summary>
        public static byte[] PackLtab(Geometry G, (int Free, int Dirty)[] Ltab)
        {
            var Buf = new byte[G.LtabSz];
            var W = new BitWriter(Buf, CRC_BYTES);
            W.Put(LTAB, TYPE_BITS);
            for (int i = 0; i < G.LptLebs; i++)
            {
                int Free = (i < Ltab.Length) ? Ltab[i].Free : 0;
                int Dirty = (i < Ltab.Length) ? Ltab[i].Dirty : 0;
                W.Put(Free, G.LptSpcBits);
                W.Put(Dirty, G.LptSpcBits);
            }
            W.Flush();
            return Finish(Buf, G.LtabSz);
        }

        /// <summary>Compute crc16 over bytes [2..len) and store it as the first 2 bytes (LE).</summary>
        private static byte[] Finish(byte[] Buf, int Len)
        {
            ushort Crc = Crc16(0xFFFF, Buf, CRC_BYTES, Len - CRC_BYTES);
            Buf[0] = (byte)Crc;
            Buf[1] = (byte)(Crc >> 8);
            return Buf;
        }

        /// <summary>LSB-first bit writer starting at a byte offset.</summary>
        private class BitWriter
        {
            private readonly byte[] Buf;
            private int BitPos;

            public BitWriter(byte[] Buf, int ByteOffset)
            {
                this.Buf = Buf;
                BitPos = ByteOffset * 8;
            }

            public void Put(int Val, int NrBits)
            {
                for (int i = 0; i < NrBits; i++)
                {
                    if (((Val >> i) & 1) != 0)
                        Buf[BitPos >> 3] |= (byte)(1 << (BitPos & 7));
                    BitPos++;
                }
            }

            public void Flush() { }
        }

        // ---- crc16 (Linux crc16, poly 0x8408 reflected, init passed in, no final xor) ----

        private static readonly ushort[] Crc16Table = BuildTable();

        private static ushort[] BuildTable()
        {
            var T = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort Crc = 0;
                ushort C = (ushort)i;
                for (int j = 0; j < 8; j++)
                {
                    if (((Crc ^ C) & 1) != 0)
                        Crc = (ushort)((Crc >> 1) ^ 0xA001);
                    else
                        Crc = (ushort)(Crc >> 1);
                    C >>= 1;
                }
                T[i] = Crc;
            }
            return T;
        }

        public static ushort Crc16(ushort Crc, byte[] Data, int Offset, int Len)
        {
            for (int i = 0; i < Len; i++)
                Crc = (ushort)((Crc >> 8) ^ Crc16Table[(Crc ^ Data[Offset + i]) & 0xFF]);
            return Crc;
        }
    }
}
