using System;
using System.Collections.Generic;

namespace NyaFs.ImageFormat.Compressors
{
    /// <summary>
    /// Raw LZ4 block compressor/decompressor (LZ4 block format, no frame).
    /// Compatible with the Linux kernel LZ4_compress_default / LZ4_decompress_safe
    /// used by f2fs compressed clusters.
    /// </summary>
    public static class Lz4Raw
    {
        private const int HashLog = 12;
        private const int HashSize = 1 << HashLog;
        private const int MinMatch = 4;
        private const int LastLiterals = 5;
        private const int MfLimit = 12;

        private static int Hash(uint v) => (int)((v * 2654435761u) >> (32 - HashLog));

        public static byte[] Compress(byte[] Src)
        {
            int n = Src.Length;
            var Dst = new List<byte>(n + 16);
            var Table = new int[HashSize];
            for (int i = 0; i < HashSize; i++)
                Table[i] = -1;

            int Anchor = 0;
            int P = 0;
            int MatchLimit = n - LastLiterals;
            int MfEnd = n - MfLimit;

            while (P <= MfEnd)
            {
                uint Seq = (uint)(Src[P] | (Src[P + 1] << 8) | (Src[P + 2] << 16) | (Src[P + 3] << 24));
                int H = Hash(Seq);
                int Ref = Table[H];
                Table[H] = P;

                if ((Ref >= 0) && ((P - Ref) <= 65535) && (Ref + MinMatch <= n) &&
                    (Src[Ref] == Src[P]) && (Src[Ref + 1] == Src[P + 1]) &&
                    (Src[Ref + 2] == Src[P + 2]) && (Src[Ref + 3] == Src[P + 3]))
                {
                    int MatchLen = MinMatch;
                    while ((P + MatchLen < MatchLimit) && (Src[Ref + MatchLen] == Src[P + MatchLen]))
                        MatchLen++;

                    int LitLen = P - Anchor;
                    int Offset = P - Ref;

                    int TokenPos = Dst.Count;
                    Dst.Add(0);
                    int LitCode = Math.Min(LitLen, 15);
                    Dst[TokenPos] = (byte)(LitCode << 4);

                    if (LitLen >= 15)
                    {
                        int Rest = LitLen - 15;
                        while (Rest >= 255) { Dst.Add(255); Rest -= 255; }
                        Dst.Add((byte)Rest);
                    }
                    for (int i = 0; i < LitLen; i++)
                        Dst.Add(Src[Anchor + i]);

                    Dst.Add((byte)(Offset & 0xFF));
                    Dst.Add((byte)((Offset >> 8) & 0xFF));

                    int MlCode = MatchLen - MinMatch;
                    if (MlCode >= 15)
                    {
                        Dst[TokenPos] |= 15;
                        int Rest = MlCode - 15;
                        while (Rest >= 255) { Dst.Add(255); Rest -= 255; }
                        Dst.Add((byte)Rest);
                    }
                    else
                        Dst[TokenPos] |= (byte)MlCode;

                    P += MatchLen;
                    Anchor = P;
                }
                else
                    P++;
            }

            // Last literals
            int LastLen = n - Anchor;
            int LPos = Dst.Count;
            Dst.Add(0);
            int LCode = Math.Min(LastLen, 15);
            Dst[LPos] = (byte)(LCode << 4);
            if (LastLen >= 15)
            {
                int Rest = LastLen - 15;
                while (Rest >= 255) { Dst.Add(255); Rest -= 255; }
                Dst.Add((byte)Rest);
            }
            for (int i = 0; i < LastLen; i++)
                Dst.Add(Src[Anchor + i]);

            return Dst.ToArray();
        }

        public static byte[] Decompress(byte[] Src, int ExpectedSize)
        {
            var Dst = new byte[ExpectedSize];
            int I = 0;
            int O = 0;

            while (I < Src.Length)
            {
                int Token = Src[I++];

                int LitLen = Token >> 4;
                if (LitLen == 15)
                {
                    int B;
                    do { B = Src[I++]; LitLen += B; } while (B == 255);
                }

                for (int i = 0; i < LitLen; i++)
                {
                    if (O >= ExpectedSize) return Dst;
                    Dst[O++] = Src[I++];
                }

                if (I >= Src.Length)
                    break;

                int Offset = Src[I] | (Src[I + 1] << 8);
                I += 2;

                int MatchLen = Token & 15;
                if (MatchLen == 15)
                {
                    int B;
                    do { B = Src[I++]; MatchLen += B; } while (B == 255);
                }
                MatchLen += MinMatch;

                int Ref = O - Offset;
                for (int i = 0; i < MatchLen; i++)
                {
                    if (O >= ExpectedSize) break;
                    Dst[O] = Dst[Ref++];
                    O++;
                }
            }

            return Dst;
        }
    }
}
