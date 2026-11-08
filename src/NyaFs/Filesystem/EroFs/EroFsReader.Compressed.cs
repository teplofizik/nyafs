using System;
using System.IO;
using System.IO.Compression;

namespace NyaFs.Filesystem.EroFs
{
    internal sealed partial class EroFsReader
    {
        private const int MapHeaderSize = 8;
        private const int FullIndexPadding = 8;
        private const int FeatureIncompatLz4ZeroPadding = 1;
        private const int AdviseCompact2B = 0x0001;
        private const int AdviseBigPcluster1 = 0x0002;
        private const int AdviseBigPcluster2 = 0x0004;
        private const int AdviseInlinePcluster = 0x0008;
        private const int AdviseInterlacedPcluster = 0x0010;
        private const int AdviseFragmentPcluster = 0x0020;
        private const int LclusterTypePlain = 0;
        private const int LclusterTypeHead1 = 1;
        private const int LclusterTypeNonHead = 2;
        private const int LclusterTypeHead2 = 3;
        private const int LclusterDeltaCblkCount = 0x0800;
        private const int LclusterPartialRef = 0x8000;
        private const int LclusterHole = 0x4000;
        private const int CompressionLz4 = 0;
        private const int CompressionLzma = 1;
        private const int CompressionDeflate = 2;
        private const int CompressionZstd = 3;
        private const uint NullBlockAddress = 0xFFFFFFFF;

        private sealed class CompressedMap
        {
            public int HeaderOffset;
            public int IndexStart;
            public int Ebase;
            public bool Compact;
            public int Advise;
            public int Algorithm0;
            public int Algorithm1;
            public int BlockBits;
            public int LClusterBits;
            public long LClusterSize;
            public long Size;
            public int TotalLclusters;
            public int InlineSize;
            public bool Lz4ZeroPadding;
            public bool HasFragments;
            public bool WholeFragment;
            public ulong FragmentOffset;
            public ulong PackedNid;
            public long TailPackOffset;
        }

        private sealed class CompressedEntry
        {
            public int Type;
            public int ClusterOffset;
            public int PhysicalBlock;
            public int CompressedBlocks;
            public int Delta0;
            public int Delta1;
            public bool Hole;
            public bool PartialRef;
            public long NextPackOffset;
        }

        private byte[] PackedFragmentData;
        private ulong PackedFragmentNid;
        private bool CompressionConfigsLoaded;
        private bool HasCompressionConfigs;
        private int AvailableCompressionAlgorithms;
        private int LzmaDictionarySize = 1 << 20;
        private int DeflateWindowBits = 15;
        private int ZstdWindowLog = 17;

        private byte[] ReadCompressedInodeData(Inode Inode)
        {
            var Map = ReadCompressedMap(Inode);
            if (Map == null)
                return Array.Empty<byte>();

            if (Map.WholeFragment)
                return ReadWholeFragment(Inode, Map);

            if (Inode.Size == 0)
                return Array.Empty<byte>();

            if (Inode.Size > Int32.MaxValue)
                throw new InvalidOperationException("EROFS: file is too large");

            long Size = (long)Inode.Size;
            var Result = new byte[(int)Size];
            int Lcn = 0;

            while (Lcn < Map.TotalLclusters)
            {
                var Head = ReadCompressedEntry(Map, Lcn);
                if ((Head == null) || (Head.Type == LclusterTypeNonHead))
                {
                    Lcn++;
                    continue;
                }

                long Start = (long)Lcn * Map.LClusterSize + Head.ClusterOffset;
                long End = GetCompressedExtentEnd(Map, Lcn, Head);
                if (End > Size)
                    End = Size;

                if ((Start >= 0) && (Start < Size) && (End > Start))
                {
                    int Length = (int)(End - Start);
                    ReadCompressedExtent(Map, Head, Lcn, Result, (int)Start, Length, End >= Size);
                }

                if (End >= Size)
                    break;

                int NextLcn = (int)(End / Map.LClusterSize);
                if (NextLcn <= Lcn)
                    NextLcn = Lcn + 1;
                Lcn = NextLcn;
            }

            return Result;
        }

        private byte[] ReadWholeFragment(Inode Inode, CompressedMap Map)
        {
            if (Map.PackedNid == 0)
            {
                NyaFs.Log.Warning(0, $"EROFS: inode {Inode.Nid} refers to a missing packed fragment inode");
                return Array.Empty<byte>();
            }

            if ((PackedFragmentData == null) || (PackedFragmentNid != Map.PackedNid))
            {
                PackedFragmentNid = Map.PackedNid;
                var PackedInode = ReadInode(Map.PackedNid);
                PackedFragmentData = ReadInodeData(PackedInode);
            }

            if (Inode.Size > Int32.MaxValue)
                throw new InvalidOperationException("EROFS: file is too large");

            var Result = new byte[(int)Inode.Size];
            long Source = (long)Map.FragmentOffset;
            int Available = (int)Math.Min((long)Result.Length, Math.Max(0L, PackedFragmentData.Length - Source));
            if (Available > 0)
                Array.Copy(PackedFragmentData, Source, Result, 0, Available);
            return Result;
        }

        private CompressedMap ReadCompressedMap(Inode Inode)
        {
            long DataEnd = (long)Inode.Offset + Inode.InodeSize + Inode.XattrSize;
            int Header = (int)Align(DataEnd, 8);
            if ((Header < 0) || (Header + MapHeaderSize > Data.Length))
            {
                NyaFs.Log.Warning(0, $"EROFS: inode {Inode.Nid} has invalid compression header");
                return null;
            }

            int Cluster = Data[Header + 7];
            if ((Cluster & 0x80) != 0)
            {
                return new CompressedMap
                {
                    HeaderOffset = Header,
                    Size = (long)Inode.Size,
                    WholeFragment = true,
                    FragmentOffset = EroFsShared.ReadU64(Data, Header) & 0x7FFFFFFFFFFFFFFFUL,
                    PackedNid = EroFsShared.ReadU64(Data, EroFsShared.SuperblockOffset + 96)
                };
            }

            int Advise = (int)EroFsShared.ReadU16(Data, Header + 4);
            bool Compact = Inode.Layout == 3;
            if (!Compact && ((Advise & AdviseCompact2B) != 0))
            {
                NyaFs.Log.Warning(0, $"EROFS: inode {Inode.Nid} uses the unsupported extent index format");
                return null;
            }

            int BlockBits = Log2(BlockSize);
            int LClusterBits = BlockBits + (Cluster & 15);
            if ((LClusterBits < BlockBits) || (LClusterBits > 30))
            {
                NyaFs.Log.Warning(0, $"EROFS: inode {Inode.Nid} has invalid logical cluster size");
                return null;
            }

            var Result = new CompressedMap
            {
                HeaderOffset = Header,
                Compact = Compact,
                Ebase = Header + MapHeaderSize,
                IndexStart = Header + MapHeaderSize + (Compact ? 0 : FullIndexPadding),
                Advise = Advise,
                Algorithm0 = Data[Header + 6] & 15,
                Algorithm1 = (Data[Header + 6] >> 4) & 15,
                BlockBits = BlockBits,
                LClusterBits = LClusterBits,
                LClusterSize = 1L << LClusterBits,
                Size = (long)Inode.Size,
                InlineSize = ((Advise & AdviseInlinePcluster) != 0) ? (int)EroFsShared.ReadU16(Data, Header + 2) : 0,
                Lz4ZeroPadding = (EroFsShared.ReadU32(Data, EroFsShared.SuperblockOffset + 80) & FeatureIncompatLz4ZeroPadding) != 0,
                HasFragments = (Advise & AdviseFragmentPcluster) != 0
            };

            Result.TotalLclusters = (int)((Inode.Size + (ulong)Result.LClusterSize - 1) / (ulong)Result.LClusterSize);
            if (Result.TotalLclusters > 0)
            {
                var Tail = ReadCompressedEntry(Result, Result.TotalLclusters - 1);
                Result.TailPackOffset = Tail != null ? Tail.NextPackOffset : 0;
            }
            if (Result.HasFragments)
                NyaFs.Log.Warning(0, $"EROFS: inode {Inode.Nid} uses fragment packing; tail fragments may be unavailable");

            return Result;
        }

        private CompressedEntry ReadCompressedEntry(CompressedMap Map, int Lcn)
        {
            if ((Lcn < 0) || (Lcn >= Map.TotalLclusters))
                return null;

            return Map.Compact ? ReadCompactEntry(Map, Lcn) : ReadFullEntry(Map, Lcn);
        }

        private CompressedEntry ReadFullEntry(CompressedMap Map, int Lcn)
        {
            long Position = (long)Map.IndexStart + (long)Lcn * 8;
            if ((Position < 0) || (Position + 8 > Data.Length))
                return null;

            int Advise = (int)EroFsShared.ReadU16(Data, (int)Position);
            var Result = new CompressedEntry
            {
                Type = Advise & 3,
                NextPackOffset = Position + 8,
                PartialRef = (Advise & LclusterPartialRef) != 0
            };

            if (Result.Type == LclusterTypeNonHead)
            {
                Result.ClusterOffset = 1 << Map.LClusterBits;
                Result.Delta0 = (int)EroFsShared.ReadU16(Data, (int)Position + 4);
                Result.Delta1 = (int)EroFsShared.ReadU16(Data, (int)Position + 6);

                if ((Result.Delta0 & LclusterDeltaCblkCount) != 0)
                {
                    Result.CompressedBlocks = Result.Delta0 & ~LclusterDeltaCblkCount;
                    Result.Delta0 = 1;
                }
            }
            else
            {
                Result.ClusterOffset = (int)EroFsShared.ReadU16(Data, (int)Position + 2);
                Result.Hole = (Advise & LclusterHole) != 0;
                Result.PhysicalBlock = Result.Hole ? -1 : (int)EroFsShared.ReadU32(Data, (int)Position + 4);
            }

            return Result;
        }

        private CompressedEntry ReadCompactEntry(CompressedMap Map, int Lcn)
        {
            int Initial = ((32 - (int)(Map.Ebase % 32)) / 4) & 7;
            int Compact2B = 0;
            if (((Map.Advise & AdviseCompact2B) != 0) && (Initial < Map.TotalLclusters))
                Compact2B = ((Map.TotalLclusters - Initial) / 16) * 16;

            long Position = Map.Ebase;
            int Index = Lcn;
            int Shift = 2;

            if (Index >= Initial)
            {
                Position += (long)Initial * 4;
                Index -= Initial;

                if (Index < Compact2B)
                {
                    Shift = 1;
                }
                else
                {
                    Position += (long)Compact2B * 2;
                    Index -= Compact2B;
                }
            }

            Position += (long)Index * (1 << Shift);

            int Vcnt = (1 << Shift) == 4 ? 2 : 16;
            int PackSize = Vcnt << Shift;
            int Lobits = Math.Max(Map.LClusterBits, 12);
            int EncodeBits = ((PackSize - 4) * 8) >> Log2(Vcnt);
            long PackStart = Position - (Position % PackSize);
            int I = (int)((Position - PackStart) >> Shift);

            if ((PackStart < 0) || (PackStart + PackSize > Data.Length))
                return null;

            int Type;
            int Lo = DecodeCompactBits(PackStart, EncodeBits * I, Lobits, out Type);
            var Result = new CompressedEntry
            {
                Type = Type,
                NextPackOffset = PackStart + PackSize
            };

            bool BigPcluster = (Map.Advise & AdviseBigPcluster1) != 0;

            if (Type == LclusterTypeNonHead)
            {
                Result.ClusterOffset = 1 << Map.LClusterBits;
                Result.Delta1 = GetCompactForwardDistance(PackStart, Vcnt, EncodeBits, Lobits, I);

                if ((Lo & LclusterDeltaCblkCount) != 0)
                {
                    if (!BigPcluster)
                        return null;

                    Result.CompressedBlocks = Lo & ~LclusterDeltaCblkCount;
                    Result.Delta0 = 1;
                }
                else if (I + 1 != Vcnt)
                {
                    Result.Delta0 = Lo;
                }
                else
                {
                    int PreviousType;
                    int PreviousLo = DecodeCompactBits(PackStart, EncodeBits * (I - 1), Lobits, out PreviousType);
                    if (PreviousType != LclusterTypeNonHead)
                        PreviousLo = 0;
                    else if ((PreviousLo & LclusterDeltaCblkCount) != 0)
                        PreviousLo = 1;

                    Result.Delta0 = PreviousLo + 1;
                }

                return Result;
            }

            Result.ClusterOffset = Lo;
            Result.Delta0 = 0;

            int Nblk;
            int J = I;
            if (!BigPcluster)
            {
                Nblk = 1;
                while (J > 0)
                {
                    J--;
                    int PreviousType;
                    int PreviousLo = DecodeCompactBits(PackStart, EncodeBits * J, Lobits, out PreviousType);
                    if (PreviousType == LclusterTypeNonHead)
                        J -= PreviousLo;

                    if (J >= 0)
                        Nblk++;
                }
            }
            else
            {
                Nblk = 0;
                while (J > 0)
                {
                    J--;
                    int PreviousType;
                    int PreviousLo = DecodeCompactBits(PackStart, EncodeBits * J, Lobits, out PreviousType);
                    if (PreviousType == LclusterTypeNonHead)
                    {
                        if ((PreviousLo & LclusterDeltaCblkCount) != 0)
                        {
                            J--;
                            Nblk += PreviousLo & ~LclusterDeltaCblkCount;
                            continue;
                        }

                        if (PreviousLo <= 1)
                            return null;

                        J -= PreviousLo - 2;
                        continue;
                    }

                    Nblk++;
                }
            }

            Result.PhysicalBlock = (int)EroFsShared.ReadU32(Data, (int)(PackStart + PackSize - 4)) + Nblk;
            return Result;
        }

        private int DecodeCompactBits(long PackStart, int BitPosition, int Lobits, out int Type)
        {
            uint Value = EroFsShared.ReadU32(Data, (int)(PackStart + (BitPosition / 8))) >> (BitPosition & 7);
            int Lo = (int)(Value & ((1U << Lobits) - 1));
            Type = (int)((Value >> Lobits) & 3);
            return Lo;
        }

        private int GetCompactForwardDistance(long PackStart, int Vcnt, int EncodeBits, int Lobits, int Index)
        {
            int Distance = 0;
            int LastLo = 0;

            do
            {
                int Type;
                LastLo = DecodeCompactBits(PackStart, EncodeBits * Index, Lobits, out Type);
                if (Type != LclusterTypeNonHead)
                    return Distance;

                Distance++;
            }
            while (++Index < Vcnt);

            if ((LastLo & LclusterDeltaCblkCount) == 0)
                Distance += LastLo - 1;

            return Math.Max(1, Distance);
        }

        private long GetCompressedExtentEnd(CompressedMap Map, int HeadLcn, CompressedEntry Head)
        {
            long Lcn = HeadLcn;
            int Guard = 0;

            while (Guard++ < Map.TotalLclusters + 1)
            {
                if (Lcn * Map.LClusterSize >= Map.Size)
                    return Map.Size;

                var Entry = ReadCompressedEntry(Map, (int)Lcn);
                if (Entry == null)
                    return Map.Size;

                if (Entry.Type == LclusterTypeNonHead)
                {
                    int Delta = Entry.Delta1;
                    if (Delta <= 0)
                        Delta = 1;
                    Lcn += Delta;
                }
                else
                {
                    if (Lcn != HeadLcn)
                        return Lcn * Map.LClusterSize + Entry.ClusterOffset;

                    Lcn++;
                }
            }

            return Map.Size;
        }

        private int GetCompressedBlocks(CompressedMap Map, CompressedEntry Head, int HeadLcn)
        {
            bool Big1 = (Map.Advise & AdviseBigPcluster1) != 0;
            bool Big2 = (Map.Advise & AdviseBigPcluster2) != 0;
            bool Last = ((long)(HeadLcn + 1) * Map.LClusterSize) >= Map.Size;

            bool Single = (Last ||
                ((Head.Type == LclusterTypeHead1) && !Big1) ||
                (((Head.Type == LclusterTypePlain) || (Head.Type == LclusterTypeHead2)) && !Big2));

            if (Single)
                return 1 << (Map.LClusterBits - Map.BlockBits);

            if (Head.CompressedBlocks > 0)
                return Head.CompressedBlocks;

            var Next = ReadCompressedEntry(Map, HeadLcn + 1);
            if ((Next == null) || (Next.Type != LclusterTypeNonHead))
                return 1 << (Map.LClusterBits - Map.BlockBits);

            if ((Next.Delta0 == 1) && (Next.CompressedBlocks > 0))
                return Next.CompressedBlocks;

            return 1 << (Map.LClusterBits - Map.BlockBits);
        }

        private void ReadCompressedExtent(CompressedMap Map, CompressedEntry Head, int HeadLcn,
            byte[] Output, int OutputOffset, int Length, bool LastExtent)
        {
            if (Head.Hole)
                return;

            bool Inline = LastExtent && (Map.InlineSize > 0);
            if (!Inline && (Head.PhysicalBlock < 0))
                return;

            if (Head.Type == LclusterTypePlain)
            {
                long Source = (long)Head.PhysicalBlock * BlockSize;
                int Available = (int)Math.Min(Length, Math.Max(0, Data.Length - Source));
                if (Available > 0)
                    Array.Copy(Data, Source, Output, OutputOffset, Available);

                if ((Map.Advise & AdviseInterlacedPcluster) != 0)
                    NyaFs.Log.Warning(0, "EROFS: interlaced plain pcluster is not fully supported");
                return;
            }

            int Algorithm = Head.Type == LclusterTypeHead2 ? Map.Algorithm1 : Map.Algorithm0;
            EnsureCompressionConfigs();
            if ((AvailableCompressionAlgorithms & (1 << Algorithm)) == 0)
            {
                NyaFs.Log.Warning(0, $"EROFS: compression algorithm {Algorithm} is not available");
                return;
            }

            int Blocks = GetCompressedBlocks(Map, Head, HeadLcn);
            long SourceOffset;
            int CompressedLength;

            if (Inline)
            {
                SourceOffset = Map.TailPackOffset;
                CompressedLength = Map.InlineSize;
            }
            else
            {
                SourceOffset = (long)Head.PhysicalBlock * BlockSize;
                CompressedLength = Blocks * BlockSize;
            }

            if ((SourceOffset < 0) || (SourceOffset >= Data.Length) || (CompressedLength <= 0))
            {
                NyaFs.Log.Warning(0, $"EROFS: compressed extent at lcluster {HeadLcn} is out of image");
                return;
            }

            CompressedLength = Math.Min(CompressedLength, (int)(Data.Length - SourceOffset));
            if (Map.Lz4ZeroPadding || (Algorithm != CompressionLz4))
            {
                int Skipped = 0;
                while ((Skipped < CompressedLength) && (Skipped < BlockSize) && (Data[SourceOffset + Skipped] == 0))
                    Skipped++;

                SourceOffset += Skipped;
                CompressedLength -= Skipped;
            }

            if (CompressedLength <= 0)
                return;

            var Decompressed = new byte[Length];
            int Decoded = DecompressExtent(Algorithm, SourceOffset, CompressedLength, Decompressed);

            if (Decoded <= 0)
            {
                NyaFs.Log.Warning(0, $"EROFS: failed to decompress algorithm {Algorithm} extent at lcluster {HeadLcn}");
                return;
            }

            Array.Copy(Decompressed, 0, Output, OutputOffset, Math.Min(Decoded, Length));
        }

        private void EnsureCompressionConfigs()
        {
            if (CompressionConfigsLoaded)
                return;

            CompressionConfigsLoaded = true;
            int Sb = EroFsShared.SuperblockOffset;
            int Incompat = (int)EroFsShared.ReadU32(Data, Sb + 80);
            HasCompressionConfigs = (Incompat & 2) != 0;
            if (!HasCompressionConfigs)
            {
                AvailableCompressionAlgorithms = 1 << CompressionLz4;
                return;
            }

            AvailableCompressionAlgorithms = (int)EroFsShared.ReadU16(Data, Sb + 84);
            int Offset = Sb + 128 + Data[Sb + 13] * 16;

            for (int Alg = 0; Alg < 4; Alg++)
            {
                if ((AvailableCompressionAlgorithms & (1 << Alg)) == 0)
                    continue;

                Offset = (int)Align(Offset, 4);
                if (Offset + 2 > Data.Length)
                    break;

                int Length = (int)EroFsShared.ReadU16(Data, Offset);
                int Payload = Offset + 2;
                if ((Length <= 0) || (Payload + Length > Data.Length))
                    break;

                switch (Alg)
                {
                    case CompressionLzma:
                        if (Length >= 4)
                        {
                            int Dict = (int)EroFsShared.ReadU32(Data, Payload);
                            if (Dict >= 4096)
                                LzmaDictionarySize = Dict;
                        }
                        break;
                    case CompressionDeflate:
                        if (Length >= 1)
                            DeflateWindowBits = Data[Payload];
                        break;
                    case CompressionZstd:
                        if (Length >= 2)
                            ZstdWindowLog = Data[Payload + 1];
                        break;
                }

                Offset = Payload + Length;
            }
        }

        private int DecompressExtent(int Algorithm, long SourceOffset, int CompressedLength, byte[] Output)
        {
            switch (Algorithm)
            {
                case CompressionLz4:
                    return FT.LZ4.LZ4Codec.Decode(Data, (int)SourceOffset, CompressedLength, Output, 0, Output.Length);

                case CompressionDeflate:
                {
                    using (var Input = new MemoryStream(Data, (int)SourceOffset, CompressedLength, false))
                    using (var Inflater = new DeflateStream(Input, CompressionMode.Decompress))
                    {
                        int Total = 0;
                        while (Total < Output.Length)
                        {
                            int Read = Inflater.Read(Output, Total, Output.Length - Total);
                            if (Read <= 0)
                                break;
                            Total += Read;
                        }
                        return Total;
                    }
                }

                case CompressionZstd:
                {
                    using (var Decompressor = new ZstdSharp.Decompressor())
                        return Decompressor.Unwrap(Data, (int)SourceOffset, CompressedLength, Output, 0, Output.Length);
                }

                case CompressionLzma:
                {
                    EnsureCompressionConfigs();
                    if (CompressedLength < 5)
                        return -1;

                    int Properties = (~Data[SourceOffset]) & 0xFF;
                    byte[] LzmaProperties =
                    {
                        (byte)Properties,
                        (byte)(LzmaDictionarySize & 0xFF),
                        (byte)((LzmaDictionarySize >> 8) & 0xFF),
                        (byte)((LzmaDictionarySize >> 16) & 0xFF),
                        (byte)((LzmaDictionarySize >> 24) & 0xFF)
                    };

                    var LzmaInput = new byte[CompressedLength];
                    LzmaInput[0] = 0;
                    Array.Copy(Data, SourceOffset + 1, LzmaInput, 1, CompressedLength - 1);

                    using (var Input = new MemoryStream(LzmaInput, false))
                    using (var Decoded = new MemoryStream(Output.Length))
                    {
                        var Decoder = new SevenZip.Compression.LZMA.Decoder();
                        Decoder.SetDecoderProperties(LzmaProperties);
                        Decoder.Code(Input, Decoded, LzmaInput.Length, Output.Length, null);
                        var Bytes = Decoded.ToArray();
                        Array.Copy(Bytes, 0, Output, 0, Math.Min(Bytes.Length, Output.Length));
                        return Math.Min(Bytes.Length, Output.Length);
                    }
                }
            }

            return -1;
        }

        private byte[] ReadChunkBasedInodeData(Inode Inode)
        {
            if (Inode.Size == 0)
                return Array.Empty<byte>();

            if (Inode.Size > Int32.MaxValue)
                throw new InvalidOperationException("EROFS: file is too large");

            int Format = (int)(Inode.RawU & 0xFFFF);
            int ChunkBits = Format & 0x1F;
            bool Indexes = (Format & 0x20) != 0;
            long ChunkSize = (long)BlockSize << ChunkBits;
            int Unit = Indexes ? 8 : 4;
            long IndexStart = Align((long)Inode.Offset + Inode.InodeSize + Inode.XattrSize, Unit);
            int Count = (int)((Inode.Size + (ulong)ChunkSize - 1) / (ulong)ChunkSize);
            var Result = new byte[(int)Inode.Size];

            for (int i = 0; i < Count; i++)
            {
                long Position = IndexStart + (long)i * Unit;
                if ((Position < 0) || (Position + Unit > Data.Length))
                    break;

                ulong Start;
                int Device = 0;

                if (Indexes)
                {
                    Start = EroFsShared.ReadU32(Data, (int)Position + 4) |
                            ((ulong)EroFsShared.ReadU16(Data, (int)Position) << 32);
                    Device = (int)EroFsShared.ReadU16(Data, (int)Position + 2);
                }
                else
                {
                    Start = EroFsShared.ReadU32(Data, (int)Position);
                }

                if (Start == NullBlockAddress)
                    continue;

                if (Device != 0)
                {
                    NyaFs.Log.Warning(0, $"EROFS: inode {Inode.Nid} chunk {i} is on external device {Device}, data is not available");
                    continue;
                }

                long Source = (long)Start * BlockSize;
                int Length = (int)Math.Min(ChunkSize, (long)Inode.Size - (long)i * ChunkSize);
                int Available = (int)Math.Min((long)Length, Math.Max(0L, Data.Length - Source));
                if (Available > 0)
                    Array.Copy(Data, Source, Result, i * (int)ChunkSize, Available);
            }

            return Result;
        }

        private static int Log2(int Value)
        {
            int Result = 0;
            while (Value > 1)
            {
                Value >>= 1;
                Result++;
            }
            return Result;
        }

        private static long Align(long Value, int Alignment) =>
            (Value + Alignment - 1) / Alignment * Alignment;
    }
}
