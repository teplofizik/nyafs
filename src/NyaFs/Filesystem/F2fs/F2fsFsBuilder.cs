using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.F2fs
{
    /// <summary>
    /// F2FS image builder: empty filesystem (mkfs-like) and writing a directory tree
    /// (inodes, dentry blocks, data blocks, direct-node blocks) with NAT/SIT/SSA/checkpoint.
    /// </summary>
    public class F2fsFsBuilder : Universal.IFilesystemBuilder, Universal.IXattrBuilder
    {
        public const int BlockSize = 4096;
        public const int BlocksPerSeg = 512;

        private const uint Magic = 0xF2F52010;
        private const uint CpVersion = 1;
        private const uint CpFlagUmount = 0x1;
        private const int NatsPerBlock = 455;
        private const int SitsPerBlock = 55;
        private const int NatEntrySize = 9;
        private const int SitEntrySize = 74;
        private const int DentryCount = 214;
        private const int DentryBase = 30;
        private const int FilenameBase = 2384;
        private const int AddrsPerInode = 923;
        private const int AddrsPerBlock = 1018;
        private const int InodeAddrOff = 0x168;
        private const int InodeNidOff = 0xFD4;
        private const int InodeXattrNidOff = 0x4C;
        private const int FooterOff = 0xFE8;
        private const int InlineXattrAddrs = 50;             // DEFAULT_INLINE_XATTR_ADDRS
        private const uint XattrMagic = 0xF2F52011;

        public readonly long TotalBytes;
        public uint RootIno = 3;
        public uint NodeIno = 1;
        public uint MetaIno = 2;
        public uint ReservedSegments = 8;
        public uint OverprovisionSegments = 10;

        /// <summary>Per-file compression for regular files (None disables F2FS_FEATURE_COMPRESSION).</summary>
        public F2fsCompression Compression = F2fsCompression.None;

        /// <summary>log2 of the compression cluster size in blocks (2..8).</summary>
        public int CompressionClusterLog = 2;

        private const int CompressionExtraIsize = 36;       // F2FS_TOTAL_EXTRA_ATTR_SIZE (bytes)
        private const uint FeatureCompression = 0x2000;
        private const uint FeatureExtraAttr = 0x0008;
        private const uint CompressAddr = 0xFFFFFFFE;

        private int Segment0Blkaddr;
        private int CpBlkaddr, SitBlkaddr, NatBlkaddr, SsaBlkaddr, MainBlkaddr;
        private int SectionCount, SegmentCount, SegCkpt, SegSit, SegNat, SegSsa, SegMain;

        private byte[] Image;

        private int NextFreeSeg;
        private int NextFreeNid;
        private readonly Dictionary<uint, long> Nat = new Dictionary<uint, long>();
        private readonly Dictionary<uint, uint> NatIno = new Dictionary<uint, uint>();
        private readonly Dictionary<int, SegData> SealedSegs = new Dictionary<int, SegData>();
        private readonly Curseg[] Cursegs = new Curseg[6];
        private int NodeBlocks;
        private int DataBlocks;
        private int InodeCount;
        private int ValidExtra;      // pseudo blocks (COMPRESS_ADDR) counted in CP valid_block_count

        private class Summary
        {
            public uint Nid;
            public byte Version;
            public ushort Ofs;
        }

        private class SegData
        {
            public int Type;
            public Summary[] Entries;
        }

        private class Curseg
        {
            public int Segno;
            public int Blkoff;
            public Summary[] Entries = new Summary[BlocksPerSeg];
        }

        private class Node
        {
            public string Name = "";
            public Node Parent;
            public bool IsDir;
            public byte Type;
            public uint Mode, Uid, Gid;
            public byte[] Data;
            public uint Rdev;
            public uint Ino;
            public readonly List<Node> Children = new List<Node>();
            public readonly Dictionary<string, byte[]> Xattrs = new Dictionary<string, byte[]>();
            public int DirLevel;
            public int[] ChildBlock;
            public int[] ChildSlot;
        }

        private Node Root;

        public F2fsFsBuilder(long TotalBytes)
        {
            this.TotalBytes = TotalBytes;
            ComputeGeometry();
            for (int i = 0; i < 6; i++)
                Cursegs[i] = new Curseg { Segno = i };
            NextFreeSeg = 6;
            NextFreeNid = (int)RootIno + 1;
        }

        // ---- public tree API ----

        public void Directory(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, 0x4000, null, 0);

        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, 0x8000, Content, 0);

        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, 0xA000, Encoding.UTF8.GetBytes(Target ?? ""), 0);

        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, 0x2000, null, EncodeDev(Major, Minor));

        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, 0x6000, null, EncodeDev(Major, Minor));

        public void Fifo(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, 0x1000, null, 0);

        public void Socket(string Path, uint User, uint Group, uint Mode) =>
            GetOrCreate(Path, User, Group, Mode, 0xC000, null, 0);

        public void Xattr(string Path, string Name, byte[] Value)
        {
            EnsureRoot();
            var N = ResolveNode(Path);
            if (N != null)
                N.Xattrs[Name] = Value ?? Array.Empty<byte>();
        }

        private Node ResolveNode(string Path)
        {
            var Parts = (Path ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var Cur = Root;
            foreach (var P in Parts)
            {
                var Next = Cur.Children.Find(C => C.Name == P);
                if (Next == null)
                    return null;
                Cur = Next;
            }
            return Cur;
        }

        private void GetOrCreate(string Path, uint User, uint Group, uint Mode, uint Type, byte[] Content, uint Rdev)
        {
            EnsureRoot();
            var Dir = ResolveParent(Path, out string Name);

            if (Name.Length == 0)
            {
                Root.Uid = User;
                Root.Gid = Group;
                Root.Mode = (Mode & 0xFFF) | 0x4000;
                return;
            }

            var Child = Dir.Children.Find(C => C.Name == Name);
            if (Child == null)
            {
                Child = new Node { Name = Name, Parent = Dir };
                Dir.Children.Add(Child);
            }

            uint FType = Type;
            Child.IsDir = FType == 0x4000;
            Child.Uid = User;
            Child.Gid = Group;
            Child.Mode = (Mode & 0xFFF) | FType;
            Child.Type = TypeToFType(FType);
            Child.Data = Content;
            Child.Rdev = Rdev;
        }

        private void EnsureRoot()
        {
            if (Root != null)
                return;
            Root = new Node
            {
                Name = "",
                IsDir = true,
                Type = 2,
                Mode = 0x41ED,
                Ino = RootIno
            };
        }

        private Node ResolveParent(string Path, out string Name)
        {
            var Parts = (Path ?? "/").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (Parts.Length == 0)
            {
                Name = "";
                return Root;
            }

            var Cur = Root;
            for (int i = 0; i < Parts.Length - 1; i++)
            {
                var Next = Cur.Children.Find(C => C.Name == Parts[i] && C.IsDir);
                if (Next == null)
                {
                    Next = new Node
                    {
                        Name = Parts[i],
                        Parent = Cur,
                        IsDir = true,
                        Type = 2,
                        Mode = 0x41ED,
                        Uid = 0,
                        Gid = 0
                    };
                    Cur.Children.Add(Next);
                }
                Cur = Next;
            }

            Name = Parts[Parts.Length - 1];
            return Cur;
        }

        private static byte TypeToFType(uint Type)
        {
            switch (Type)
            {
                case 0x4000: return 2;
                case 0x2000: return 3;
                case 0x6000: return 4;
                case 0x1000: return 5;
                case 0xC000: return 6;
                case 0xA000: return 7;
                default: return 1;
            }
        }

        private static uint EncodeDev(uint Major, uint Minor) =>
            (Minor & 0xFF) | (Major << 8) | ((Minor & ~0xFFu) << 12);

        // ---- geometry ----
        // NOTE: fixed geometry (segs_per_sec=1, secs_per_zone=1, constant rsvd/overprov)
        // is only valid for small images (< 512 MiB); larger images need mkfs-like
        // section/zone/overprovision calculation.

        private static int AlignUp(int V, int A) => ((V + A - 1) / A) * A;

        private void ComputeGeometry()
        {
            int SegmentSizeBytes = BlockSize * BlocksPerSeg;
            int ZoneSizeBytes = SegmentSizeBytes;

            int ZoneAlign = ((2 * BlockSize + ZoneSizeBytes - 1) / ZoneSizeBytes) * ZoneSizeBytes;
            Segment0Blkaddr = ZoneAlign / BlockSize;
            CpBlkaddr = Segment0Blkaddr;

            SegmentCount = (int)((TotalBytes - ZoneAlign) / SegmentSizeBytes);
            SegCkpt = 2;

            int BlocksForSit = AlignUp(SegmentCount, SitsPerBlock);
            SegSit = ((BlocksForSit + BlocksPerSeg - 1) / BlocksPerSeg) * 2;

            int BlocksForNat = AlignUp(SegmentCount, NatsPerBlock);
            SegNat = ((BlocksForNat + BlocksPerSeg - 1) / BlocksPerSeg) * 2;

            int AfterMeta = SegmentCount - (SegCkpt + SegSit + SegNat);
            SegSsa = (AfterMeta + BlocksPerSeg - 1) / BlocksPerSeg;

            SitBlkaddr = Segment0Blkaddr + SegCkpt * BlocksPerSeg;
            NatBlkaddr = SitBlkaddr + SegSit * BlocksPerSeg;
            SsaBlkaddr = NatBlkaddr + SegNat * BlocksPerSeg;
            MainBlkaddr = SsaBlkaddr + SegSsa * BlocksPerSeg;

            int TotalMeta = SegCkpt + SegSit + SegNat + SegSsa;
            SectionCount = SegmentCount - TotalMeta;
            SegMain = SectionCount;
        }

        // ---- build ----

        public byte[] GetFilesystemImage()
        {
            Image = new byte[TotalBytes];
            EnsureRoot();
            Root.Ino = RootIno;

            AssignInodes(Root);

            WriteSuperblock();
            WriteTree(Root);
            FinalizeImage();

            return Image;
        }

        private void AssignInodes(Node N)
        {
            if (N != Root)
                N.Ino = (uint)NextFreeNid++;
            InodeCount++;

            foreach (var C in N.Children)
                AssignInodes(C);
        }

        private void WriteTree(Node N)
        {
            if (N.IsDir)
                WriteDirectory(N);
            else
                WriteFileLike(N);

            foreach (var C in N.Children)
                WriteTree(C);
        }

        // ---- directories ----

        private void WriteDirectory(Node D)
        {
            int N = D.Children.Count;
            var ChildBlock = new int[N];
            var ChildSlot = new int[N];
            var Used = new Dictionary<int, bool[]>();

            bool[] GetBlock(int b)
            {
                if (!Used.TryGetValue(b, out var u))
                {
                    u = new bool[DentryCount];
                    Used[b] = u;
                }
                return u;
            }

            GetBlock(0)[0] = true;
            GetBlock(0)[1] = true;

            int CurrentDepth = 1;

            for (int i = 0; i < N; i++)
            {
                var Nb = NameBytes(D.Children[i].Name);
                int Ns = (Nb.Length + 7) / 8;
                if (Ns > DentryCount)
                    throw new ArgumentException($"F2FS: name too long in '{D.Name}'");

                uint Hash = TeaHash(Nb);
                bool Placed = false;

                for (int Level = 0; Level < CurrentDepth && !Placed; Level++)
                {
                    int Buckets = 1 << Level;
                    int Bucket = (int)(Hash % (uint)Buckets);
                    int Base = 2 * (Buckets - 1) + 2 * Bucket;

                    for (int b = Base; b < Base + 2 && !Placed; b++)
                    {
                        var U = GetBlock(b);
                        int Pos = FindRoom(U, Ns);
                        if (Pos >= 0)
                        {
                            for (int k = 0; k < Ns; k++) U[Pos + k] = true;
                            ChildBlock[i] = b;
                            ChildSlot[i] = Pos;
                            Placed = true;
                        }
                    }
                }

                if (!Placed)
                {
                    int Level = CurrentDepth++;
                    int Buckets = 1 << Level;
                    int Bucket = (int)(Hash % (uint)Buckets);
                    int Base = 2 * (Buckets - 1) + 2 * Bucket;
                    var U = GetBlock(Base);
                    int Pos = FindRoom(U, Ns);
                    if (Pos < 0)
                        throw new ArgumentException($"F2FS: no room in directory '{D.Name}'");
                    for (int k = 0; k < Ns; k++) U[Pos + k] = true;
                    ChildBlock[i] = Base;
                    ChildSlot[i] = Pos;
                }
            }

            int MaxBlock = 0;
            foreach (var KV in Used)
                if (KV.Key > MaxBlock)
                    MaxBlock = KV.Key;

            var Inode = NewInode(D);
            int XattrAddrs = AddXattrs(D, Inode, out int ExtraXattrBlocks);
            int Cap = AddrsPerInode - XattrAddrs;

            if (MaxBlock + 1 > Cap)
                throw new ArgumentException("F2FS: directory too large");

            var Blocks = new byte[MaxBlock + 1][];
            for (int b = 0; b <= MaxBlock; b++)
            {
                Blocks[b] = new byte[BlockSize];
                MarkDirSlots(Blocks[b], GetBlock(b));
            }

            WriteDentry(Blocks[0], 0, NameBytes("."), D.Ino, 0, 2);
            WriteDentry(Blocks[0], 1, NameBytes(".."), D.Parent?.Ino ?? RootIno, 0, 2);

            for (int i = 0; i < N; i++)
            {
                var C = D.Children[i];
                var Nb = NameBytes(C.Name);
                WriteDentry(Blocks[ChildBlock[i]], ChildSlot[i], Nb, C.Ino, TeaHash(Nb), C.Type);
            }

            for (int b = 0; b <= MaxBlock; b++)
            {
                long Addr = AllocData(D.Ino, (ushort)b);
                WriteBlock(Addr, Blocks[b]);
                PutU32(Inode, InodeAddrOff + b * 4, (uint)Addr);
            }

            PutU64(Inode, 0x10, (ulong)(MaxBlock + 1) * BlockSize);
            PutU64(Inode, 0x18, (ulong)(1 + MaxBlock + 1 + ExtraXattrBlocks));
            PutU32(Inode, 0x48, (uint)CurrentDepth);
            Inode[0x15B] = 0;

            WriteInode(D, Inode);
        }

        private static void WriteDentry(byte[] Blk, int Pos, byte[] Name, uint Ino, uint Hash, byte FType)
        {
            PutU32(Blk, DentryBase + Pos * 11 + 0, Hash);
            PutU32(Blk, DentryBase + Pos * 11 + 4, Ino);
            PutU16(Blk, DentryBase + Pos * 11 + 8, (ushort)Name.Length);
            Blk[DentryBase + Pos * 11 + 10] = FType;
            for (int i = 0; i < Name.Length; i++)
                Blk[FilenameBase + Pos * 8 + i] = Name[i];
        }

        private static void MarkDirSlots(byte[] Blk, bool[] Used)
        {
            for (int i = 0; i < DentryCount; i++)
            {
                if (Used[i])
                    Blk[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        private static int FindRoom(bool[] Used, int Slots)
        {
            for (int Start = 0; Start + Slots <= DentryCount; Start++)
            {
                bool Free = true;
                for (int k = 0; k < Slots; k++)
                {
                    if (Used[Start + k]) { Free = false; break; }
                }
                if (Free)
                    return Start;
            }
            return -1;
        }

        // ---- files / symlinks / special ----

        private void WriteFileLike(Node F)
        {
            var Inode = NewInode(F);
            byte[] Data = F.Data ?? Array.Empty<byte>();

            int XattrAddrs = AddXattrs(F, Inode, out int ExtraXattrBlocks);
            int D = AddrsPerInode - XattrAddrs;

            if (F.Type >= 3 && F.Type <= 6)
            {
                PutU32(Inode, InodeAddrOff, F.Rdev);
                PutU64(Inode, 0x10, 0);
                PutU64(Inode, 0x18, (ulong)(1 + ExtraXattrBlocks));
                WriteInode(F, Inode);
                return;
            }

            if (F.Type == 7 && (Data.Length <= (D - 1) * 4))
            {
                Inode[3] |= 0x0A;                    // F2FS_INLINE_DATA | F2FS_DATA_EXIST
                Array.Copy(Data, 0, Inode, InodeAddrOff + 4, Data.Length);
                PutU64(Inode, 0x10, (ulong)Data.Length);
                PutU64(Inode, 0x18, (ulong)(1 + ExtraXattrBlocks));
                WriteInode(F, Inode);
                return;
            }

            if ((Compression != F2fsCompression.None) && (F.Type == 1) && (Data.Length > 0) &&
                TryCompressFile(F, Data, Inode, XattrAddrs, ExtraXattrBlocks))
                return;

            int Count = (Data.Length + BlockSize - 1) / BlockSize;
            int B = AddrsPerBlock;
            long IND = (long)B * B;
            long Base1 = D + 2L * B;
            long Base2 = Base1 + IND;
            long Base3 = Base2 + IND;
            if (Count > Base3 + (long)B * B * B)
                throw new ArgumentException($"F2FS: file '{F.Name}' is too large");

            // Plan node nids first so that data-block summaries reference the owning node.
            int NidStart = NextFreeNid;
            uint NidDir1 = 0, NidDir2 = 0, NidInd1 = 0, NidInd2 = 0, NidDind = 0;
            var Ind1Dnodes = new List<uint>();
            var Ind2Dnodes = new List<uint>();
            var DindIndirects = new List<uint>();
            var DindDnodes = new List<List<uint>>();

            if (Count > D) NidDir1 = (uint)NextFreeNid++;
            if (Count > D + B) NidDir2 = (uint)NextFreeNid++;

            if (Count > Base1)
            {
                long Total = Math.Min(Count - Base1, IND);
                int Groups = (int)((Total + B - 1) / B);
                for (int g = 0; g < Groups; g++) Ind1Dnodes.Add((uint)NextFreeNid++);
                NidInd1 = (uint)NextFreeNid++;
            }

            if (Count > Base2)
            {
                long Total = Math.Min(Count - Base2, IND);
                int Groups = (int)((Total + B - 1) / B);
                for (int g = 0; g < Groups; g++) Ind2Dnodes.Add((uint)NextFreeNid++);
                NidInd2 = (uint)NextFreeNid++;
            }

            if (Count > Base3)
            {
                long Remaining = Count - Base3;
                long Groups1 = (Remaining + IND - 1) / IND;
                for (long g1 = 0; g1 < Groups1; g1++)
                {
                    long R1 = Math.Min(Remaining - g1 * IND, IND);
                    int Groups2 = (int)((R1 + B - 1) / B);
                    var Dnids = new List<uint>();
                    for (int g2 = 0; g2 < Groups2; g2++) Dnids.Add((uint)NextFreeNid++);
                    DindDnodes.Add(Dnids);
                    DindIndirects.Add((uint)NextFreeNid++);
                }
                NidDind = (uint)NextFreeNid++;
            }

            int NodeBlocksUsed = NextFreeNid - NidStart;

            var A = new long[Count];
            for (int i = 0; i < Count; i++)
            {
                uint Parent = F.Ino;
                int Ofs;
                if (i < D) { Ofs = i; }
                else if (i < D + B) { Parent = NidDir1; Ofs = i - D; }
                else if (i < D + 2 * B) { Parent = NidDir2; Ofs = i - (D + B); }
                else if (i < Base2)
                {
                    int g = (int)((i - Base1) / B);
                    Parent = Ind1Dnodes[g];
                    Ofs = (int)((i - Base1) % B);
                }
                else if (i < Base3)
                {
                    int g = (int)((i - Base2) / B);
                    Parent = Ind2Dnodes[g];
                    Ofs = (int)((i - Base2) % B);
                }
                else
                {
                    long r = i - Base3;
                    int g1 = (int)(r / IND);
                    long rr = r % IND;
                    int g2 = (int)(rr / B);
                    Parent = DindDnodes[g1][g2];
                    Ofs = (int)(rr % B);
                }

                A[i] = AllocData(Parent, (ushort)Ofs);
                WriteBlock(A[i], Chunk(Data, i));
            }

            for (int i = 0; i < Math.Min(Count, D); i++)
                PutU32(Inode, InodeAddrOff + i * 4, (uint)A[i]);

            if (NidDir1 != 0)
            {
                WriteNodeBody(F.Ino, NidDir1, A, D, Math.Min(Count - D, B));
                PutU32(Inode, InodeNidOff + 0, NidDir1);
            }

            if (NidDir2 != 0)
            {
                WriteNodeBody(F.Ino, NidDir2, A, D + B, (int)Math.Min(Count - (D + B), B));
                PutU32(Inode, InodeNidOff + 4, NidDir2);
            }

            if (NidInd1 != 0)
            {
                long Total = Math.Min(Count - Base1, IND);
                for (int g = 0; g < Ind1Dnodes.Count; g++)
                    WriteNodeBody(F.Ino, Ind1Dnodes[g], A, (int)(Base1 + g * B), (int)Math.Min(B, Total - (long)g * B));
                WriteIndirectBody(F.Ino, NidInd1, Ind1Dnodes);
                PutU32(Inode, InodeNidOff + 8, NidInd1);
            }

            if (NidInd2 != 0)
            {
                long Total = Math.Min(Count - Base2, IND);
                for (int g = 0; g < Ind2Dnodes.Count; g++)
                    WriteNodeBody(F.Ino, Ind2Dnodes[g], A, (int)(Base2 + g * B), (int)Math.Min(B, Total - (long)g * B));
                WriteIndirectBody(F.Ino, NidInd2, Ind2Dnodes);
                PutU32(Inode, InodeNidOff + 12, NidInd2);
            }

            if (NidDind != 0)
            {
                long Remaining = Count - Base3;
                for (int g1 = 0; g1 < DindDnodes.Count; g1++)
                {
                    long R1 = Math.Min(Remaining - (long)g1 * IND, IND);
                    for (int g2 = 0; g2 < DindDnodes[g1].Count; g2++)
                        WriteNodeBody(F.Ino, DindDnodes[g1][g2], A, (int)(Base3 + (long)g1 * IND + g2 * B), (int)Math.Min(B, R1 - (long)g2 * B));
                    WriteIndirectBody(F.Ino, DindIndirects[g1], DindDnodes[g1]);
                }
                WriteIndirectBody(F.Ino, NidDind, DindIndirects);
                PutU32(Inode, InodeNidOff + 16, NidDind);
            }

            PutU64(Inode, 0x10, (ulong)Data.Length);
            PutU64(Inode, 0x18, (ulong)(1 + Count + NodeBlocksUsed + ExtraXattrBlocks));
            WriteInode(F, Inode);
        }

        private void WriteNodeBody(uint Ino, uint Nid, long[] A, int Start, int Count)
        {
            var Blk = new byte[BlockSize];
            for (int i = 0; i < Count; i++)
                PutU32(Blk, i * 4, (uint)A[Start + i]);
            long Addr = AllocNode(Nid, Ino);
            WriteNodeBlock(Addr, Nid, Ino, Blk);
        }

        private void WriteIndirectBody(uint Ino, uint Nid, List<uint> Nids)
        {
            var Blk = new byte[BlockSize];
            for (int i = 0; i < Nids.Count; i++)
                PutU32(Blk, i * 4, Nids[i]);
            long Addr = AllocNode(Nid, Ino);
            WriteNodeBlock(Addr, Nid, Ino, Blk);
        }

        private bool TryCompressFile(Node F, byte[] Data, byte[] Inode, int XattrAddrs, int ExtraXattrBlocks)
        {
            int C = 1 << CompressionClusterLog;
            int ExtraEntries = CompressionExtraIsize / 4;
            int Dc = AlignDown(AddrsPerInode - ExtraEntries - XattrAddrs, C);
            int Bc = AlignDown(AddrsPerBlock, C);
            long IND = (long)Bc * Bc;

            long R1 = Dc, R2 = Dc + Bc, R3 = Dc + 2L * Bc;
            long R4 = R3 + IND, R5 = R4 + IND;
            long MaxCap = R5 + (long)Bc * Bc * Bc;

            int Count = (Data.Length + BlockSize - 1) / BlockSize;
            int Clusters = (Count + C - 1) / C;
            long MaxBlock = (long)Clusters * C;
            if (MaxBlock > MaxCap)
                return false;

            uint Nid1 = 0, Nid2 = 0, Ind1 = 0, Ind2 = 0, Dind = 0;
            List<uint> Ind1Dn = null, Ind2Dn = null, DindInd = null;
            List<List<uint>> DindDn = null;
            var DnAddr = new Dictionary<uint, uint[]>();

            uint AllocDnode()
            {
                uint N = (uint)NextFreeNid++;
                DnAddr[N] = new uint[Bc];
                return N;
            }

            if (MaxBlock > Dc) Nid1 = AllocDnode();
            if (MaxBlock > R2) Nid2 = AllocDnode();

            if (MaxBlock > R3)
            {
                long Last = Math.Min(MaxBlock, R4);
                int G = (int)((Last - R3 + Bc - 1) / Bc);
                Ind1Dn = new List<uint>();
                for (int i = 0; i < G; i++) Ind1Dn.Add(AllocDnode());
                Ind1 = (uint)NextFreeNid++;
            }
            if (MaxBlock > R4)
            {
                long Last = Math.Min(MaxBlock, R5);
                int G = (int)((Last - R4 + Bc - 1) / Bc);
                Ind2Dn = new List<uint>();
                for (int i = 0; i < G; i++) Ind2Dn.Add(AllocDnode());
                Ind2 = (uint)NextFreeNid++;
            }
            if (MaxBlock > R5)
            {
                long Rem = MaxBlock - R5;
                long G1 = (Rem + IND - 1) / IND;
                DindDn = new List<List<uint>>();
                DindInd = new List<uint>();
                for (long i = 0; i < G1; i++)
                {
                    long R = Math.Min(Rem - i * IND, IND);
                    int G2 = (int)((R + Bc - 1) / Bc);
                    var L = new List<uint>();
                    for (int j = 0; j < G2; j++) L.Add(AllocDnode());
                    DindDn.Add(L);
                    DindInd.Add((uint)NextFreeNid++);
                }
                Dind = (uint)NextFreeNid++;
            }

            int DindDnCount = 0;
            if (DindDn != null)
                foreach (var L in DindDn)
                    DindDnCount += L.Count;

            int Nodes = 1
                + (Nid1 != 0 ? 1 : 0) + (Nid2 != 0 ? 1 : 0)
                + (Ind1Dn != null ? Ind1Dn.Count + 1 : 0)
                + (Ind2Dn != null ? Ind2Dn.Count + 1 : 0)
                + (Dind != 0 ? DindDnCount + DindInd.Count + 1 : 0);

            Inode[3] |= 0x20;                                   // F2FS_EXTRA_ATTR
            PutU16(Inode, InodeAddrOff, CompressionExtraIsize);
            PutU32(Inode, 0x50, ReadU32(Inode, 0x50) | 0x4);    // F2FS_COMPR_FL
            Inode[InodeAddrOff + 32] = (byte)Compression;       // i_compress_algorithm
            Inode[InodeAddrOff + 33] = (byte)CompressionClusterLog;// i_log_cluster_size
            PutU16(Inode, InodeAddrOff + 34, 0);                // i_compress_flag

            int DataBlocks = 0;
            int PseudoBlocks = 0;
            long ComprClusters = 0;

            var Cluster = new byte[C * BlockSize];
            for (int ci = 0; ci < Clusters; ci++)
            {
                long S = (long)ci * C;
                bool InInode;
                uint Parent;
                uint[] Arr = null;
                int Ofs;

                if (S < Dc) { InInode = true; Parent = F.Ino; Ofs = (int)S; }
                else if (S < R2) { InInode = false; Parent = Nid1; Arr = DnAddr[Nid1]; Ofs = (int)(S - Dc); }
                else if (S < R3) { InInode = false; Parent = Nid2; Arr = DnAddr[Nid2]; Ofs = (int)(S - R2); }
                else if (S < R4) { InInode = false; long lo = S - R3; int dg = (int)(lo / Bc); Parent = Ind1Dn[dg]; Arr = DnAddr[Parent]; Ofs = (int)(lo % Bc); }
                else if (S < R5) { InInode = false; long lo = S - R4; int dg = (int)(lo / Bc); Parent = Ind2Dn[dg]; Arr = DnAddr[Parent]; Ofs = (int)(lo % Bc); }
                else
                {
                    long lo = S - R5;
                    int g1i = (int)(lo / IND);
                    long rr = lo % IND;
                    int g2 = (int)(rr / Bc);
                    InInode = false;
                    Parent = DindDn[g1i][g2];
                    Arr = DnAddr[Parent];
                    Ofs = (int)(rr % Bc);
                }

                Array.Clear(Cluster, 0, Cluster.Length);
                for (int j = 0; j < C; j++)
                {
                    long Src = (S + j) * BlockSize;
                    if (Src < Data.Length)
                    {
                        int Len = (int)Math.Min(BlockSize, Data.Length - Src);
                        Array.Copy(Data, Src, Cluster, j * BlockSize, Len);
                    }
                }

                var Comp = CompressCluster(Cluster);
                int Need = 24 + Comp.Length;
                int N = (Need + BlockSize - 1) / BlockSize;

                if (N < C)
                {
                    var Payload = new byte[N * BlockSize];
                    PutU32(Payload, 0, (uint)Comp.Length);
                    Array.Copy(Comp, 0, Payload, 24, Comp.Length);

                    SetCompressedSlot(Inode, Arr, InInode, ExtraEntries, Ofs, CompressAddr);
                    for (int i = 0; i < N; i++)
                    {
                        long A = AllocData(Parent, (ushort)(Ofs + 1 + i));
                        WriteBlock(A, Chunk(Payload, i));
                        SetCompressedSlot(Inode, Arr, InInode, ExtraEntries, Ofs + 1 + i, (uint)A);
                    }
                    DataBlocks += N;
                    PseudoBlocks += 1;
                    ComprClusters += 1;
                }
                else
                {
                    for (int j = 0; j < C; j++)
                    {
                        long A = AllocData(Parent, (ushort)(Ofs + j));
                        WriteBlock(A, Chunk(Cluster, j));
                        SetCompressedSlot(Inode, Arr, InInode, ExtraEntries, Ofs + j, (uint)A);
                    }
                    DataBlocks += C;
                }
            }

            if (Nid1 != 0) { WriteDnode(F.Ino, Nid1, DnAddr[Nid1], Bc); PutU32(Inode, InodeNidOff + 0, Nid1); }
            if (Nid2 != 0) { WriteDnode(F.Ino, Nid2, DnAddr[Nid2], Bc); PutU32(Inode, InodeNidOff + 4, Nid2); }
            if (Ind1 != 0)
            {
                foreach (var n in Ind1Dn) WriteDnode(F.Ino, n, DnAddr[n], Bc);
                WriteIndirectBody(F.Ino, Ind1, Ind1Dn);
                PutU32(Inode, InodeNidOff + 8, Ind1);
            }
            if (Ind2 != 0)
            {
                foreach (var n in Ind2Dn) WriteDnode(F.Ino, n, DnAddr[n], Bc);
                WriteIndirectBody(F.Ino, Ind2, Ind2Dn);
                PutU32(Inode, InodeNidOff + 12, Ind2);
            }
            if (Dind != 0)
            {
                for (int i = 0; i < DindDn.Count; i++)
                {
                    foreach (var n in DindDn[i]) WriteDnode(F.Ino, n, DnAddr[n], Bc);
                    WriteIndirectBody(F.Ino, DindInd[i], DindDn[i]);
                }
                WriteIndirectBody(F.Ino, Dind, DindInd);
                PutU32(Inode, InodeNidOff + 16, Dind);
            }

            PutU64(Inode, 0x10, (ulong)Data.Length);
            PutU64(Inode, InodeAddrOff + 24, (ulong)ComprClusters);
            PutU64(Inode, 0x18, (ulong)(Nodes + DataBlocks + PseudoBlocks + ExtraXattrBlocks));

            ValidExtra += PseudoBlocks;
            WriteInode(F, Inode);
            return true;
        }

        private void WriteDnode(uint Ino, uint Nid, uint[] Addr, int Count)
        {
            var Blk = new byte[BlockSize];
            for (int i = 0; i < Count; i++)
                PutU32(Blk, i * 4, Addr[i]);
            long A = AllocNode(Nid, Ino);
            WriteNodeBlock(A, Nid, Ino, Blk);
        }

        private byte[] CompressCluster(byte[] Data)
        {
            switch (Compression)
            {
                case F2fsCompression.Zstd:
                    return NyaFs.ImageFormat.Compressors.ZStd.CompressWithHeader(Data);
                case F2fsCompression.Lz4:
                    return NyaFs.ImageFormat.Compressors.Lz4Raw.Compress(Data);
                case F2fsCompression.Lzo:
                case F2fsCompression.LzoRle:
                    return NyaLZO.LZO1xCompressor.Compress(Data);
                default:
                    throw new NotSupportedException($"F2FS compression algorithm {Compression} is not implemented");
            }
        }

        private static void SetCompressedSlot(byte[] Inode, uint[] Arr, bool InInode, int ExtraEntries, int Slot, uint Value)
        {
            if (InInode)
                PutU32(Inode, InodeAddrOff + (ExtraEntries + Slot) * 4, Value);
            else
                Arr[Slot] = Value;
        }

        private static long[] ToLong(uint[] Src, int Count)
        {
            var Res = new long[Count];
            for (int i = 0; i < Count; i++)
                Res[i] = Src[i];
            return Res;
        }

        private static int AlignDown(int V, int A) => (V / A) * A;

        private static uint ReadU32(byte[] D, int O) => BitConverter.ToUInt32(D, O);

        private static byte[] Chunk(byte[] Data, int Index)
        {
            var Blk = new byte[BlockSize];
            int Off = Index * BlockSize;
            int Len = Math.Min(BlockSize, Data.Length - Off);
            if (Len > 0)
                Array.Copy(Data, Off, Blk, 0, Len);
            return Blk;
        }

        // ---- inodes ----

        private byte[] NewInode(Node N)
        {
            var Inode = new byte[BlockSize];
            PutU16(Inode, 0x00, (ushort)N.Mode);
            PutU32(Inode, 0x04, N.Uid);
            PutU32(Inode, 0x08, N.Gid);
            PutU32(Inode, 0x0C, (uint)(N.IsDir ? 2 + CountSubdirs(N) : 1));
            PutU32(Inode, 0x54, N.Parent?.Ino ?? 0);

            var Nb = NameBytes(N.Name);
            PutU32(Inode, 0x58, (uint)Nb.Length);
            Array.Copy(Nb, 0, Inode, 0x5C, Math.Min(Nb.Length, 255));
            return Inode;
        }

        private static int CountSubdirs(Node D)
        {
            int N = 0;
            foreach (var C in D.Children)
                if (C.IsDir)
                    N++;
            return N;
        }

        private void WriteInode(Node N, byte[] Inode)
        {
            long Addr = AllocNode(N.Ino, N.Ino);
            WriteNodeBlock(Addr, N.Ino, N.Ino, Inode);
        }

        private void WriteNodeBlock(long Addr, uint Nid, uint Ino, byte[] Body, uint Flag = 0)
        {
            var Blk = new byte[BlockSize];
            Array.Copy(Body, Blk, BlockSize);
            PutU32(Blk, FooterOff + 0, Nid);
            PutU32(Blk, FooterOff + 4, Ino);
            PutU32(Blk, FooterOff + 8, Flag);
            PutU64(Blk, FooterOff + 12, CpVersion);
            PutU32(Blk, FooterOff + 20, (uint)(Addr + 1));
            WriteBlock(Addr, Blk);
        }

        // ---- allocation ----

        private long AllocNode(uint Nid, uint Ino)
        {
            var C = Cursegs[3];
            if (C.Blkoff >= BlocksPerSeg)
                Seal(3);

            int Off = C.Blkoff++;
            C.Entries[Off] = new Summary { Nid = Nid, Version = 0, Ofs = 0 };
            long Addr = MainBlkaddr + C.Segno * BlocksPerSeg + Off;
            Nat[Nid] = Addr;
            NatIno[Nid] = Ino;
            NodeBlocks++;
            return Addr;
        }

        private long AllocData(uint Ino, ushort Ofs)
        {
            var C = Cursegs[0];
            if (C.Blkoff >= BlocksPerSeg)
                Seal(0);

            int Off = C.Blkoff++;
            C.Entries[Off] = new Summary { Nid = Ino, Version = 0, Ofs = Ofs };
            DataBlocks++;
            return MainBlkaddr + C.Segno * BlocksPerSeg + Off;
        }

        private void Seal(int Index)
        {
            var C = Cursegs[Index];
            SealedSegs[C.Segno] = new SegData
            {
                Type = Index,
                Entries = (Summary[])C.Entries.Clone()
            };

            int Next = NextFreeSeg++;
            if (Next >= SegMain)
                throw new ArgumentException("F2FS: image is too small for the requested tree");

            C.Segno = Next;
            C.Blkoff = 0;
            C.Entries = new Summary[BlocksPerSeg];
        }

        private void WriteBlock(long Addr, byte[] Data)
        {
            Array.Copy(Data, 0, Image, Addr * BlockSize, BlockSize);
        }

        // ---- finalize ----

        private void FinalizeImage()
        {
            for (int i = 0; i < 6; i++)
            {
                var C = Cursegs[i];
                WriteSit(C.Segno, i, C.Blkoff);
                WriteSsa(C.Segno, i, C.Entries, C.Blkoff);
            }

            foreach (var KV in SealedSegs)
            {
                WriteSit(KV.Key, KV.Value.Type, BlocksPerSeg);
                WriteSsa(KV.Key, KV.Value.Type, KV.Value.Entries, BlocksPerSeg);
            }

            WriteNatArea();

            WriteCheckpointPack(CpBlkaddr, 1);
            WriteCheckpointPack(CpBlkaddr + BlocksPerSeg, 0);
        }

        private void WriteCheckpointPack(int Base, uint Version)
        {
            var Cp = BuildCheckpoint(Version);
            Array.Copy(Cp, 0, Image, (long)Base * BlockSize, BlockSize);
            for (int i = 0; i < 6; i++)
                WriteSummaryBlock(Base + 1 + i, i, Cursegs[i]);
            Array.Copy(Cp, 0, Image, (long)(Base + 7) * BlockSize, BlockSize);
        }

        private void WriteSit(int Segno, int Type, int Count)
        {
            int PerCopy = (SegSit / 2) * BlocksPerSeg;
            var Map = new byte[64];
            for (int i = 0; i < Count; i++)
                Map[i / 8] |= (byte)(0x80 >> (i % 8));

            for (int Copy = 0; Copy < 2; Copy++)
            {
                int Block = SitBlkaddr + Copy * PerCopy + Segno / SitsPerBlock;
                long Off = (long)Block * BlockSize + (Segno % SitsPerBlock) * SitEntrySize;
                PutU16(Image, Off + 0, (ushort)((Type << 10) | Count));
                Array.Copy(Map, 0, Image, Off + 2, 64);
                PutU64(Image, Off + 66, 0);
            }
        }

        private void WriteSsa(int Segno, int Type, Summary[] Entries, int Count)
        {
            long Block = (long)(SsaBlkaddr + Segno) * BlockSize;
            var Buf = new byte[BlockSize];
            for (int i = 0; i < Count; i++)
            {
                var S = Entries[i];
                if (S == null)
                    continue;
                PutU32(Buf, i * 7 + 0, S.Nid);
                Buf[i * 7 + 4] = S.Version;
                PutU16(Buf, i * 7 + 5, S.Ofs);
            }
            Buf[4091] = (byte)(Type < 3 ? 0 : 1);
            Array.Copy(Buf, 0, Image, Block, BlockSize);
        }

        private void WriteSummaryBlock(int BlkAddr, int Type, Curseg C)
        {
            var Buf = new byte[BlockSize];
            for (int i = 0; i < C.Blkoff; i++)
            {
                var S = C.Entries[i];
                if (S == null)
                    continue;
                PutU32(Buf, i * 7 + 0, S.Nid);
                Buf[i * 7 + 4] = S.Version;
                PutU16(Buf, i * 7 + 5, S.Ofs);
            }
            Buf[4091] = (byte)(Type < 3 ? 0 : 1);
            Array.Copy(Buf, 0, Image, (long)BlkAddr * BlockSize, BlockSize);
        }

        private void WriteNatArea()
        {
            int PerCopy = (SegNat / 2) * BlocksPerSeg;

            for (uint Nid = 0; Nid < (uint)NextFreeNid; Nid++)
            {
                uint Ino;
                long Blk;

                if (Nid == NodeIno) { Ino = NodeIno; Blk = 1; }
                else if (Nid == MetaIno) { Ino = MetaIno; Blk = 1; }
                else if (!Nat.TryGetValue(Nid, out Blk)) continue;
                else Ino = NatIno.TryGetValue(Nid, out uint I) ? I : Nid;

                int Bo = (int)(Nid / NatsPerBlock);
                int SlotOff = (int)(Nid % NatsPerBlock) * NatEntrySize;

                for (int Copy = 0; Copy < 2; Copy++)
                {
                    long Block = NatBlkaddr + Copy * PerCopy + Bo;
                    long Off = Block * BlockSize + SlotOff;
                    Image[Off + 0] = 0;
                    PutU32(Image, Off + 1, Ino);
                    PutU32(Image, Off + 5, (uint)Blk);
                }
            }
        }

        private byte[] BuildCheckpoint(uint Version)
        {
            int SitBmBytes = (SegSit / 2) * BlocksPerSeg / 8;
            int NatBmBytes = (SegNat / 2) * BlocksPerSeg / 8;
            var Cp = new byte[BlockSize];
            uint Total = (uint)(NodeBlocks + DataBlocks + ValidExtra);

            PutU64(Cp, 0x00, Version);
            PutU64(Cp, 0x08, (ulong)(SegMain - (int)OverprovisionSegments) * BlocksPerSeg);
            PutU64(Cp, 0x10, Total);
            PutU32(Cp, 0x18, ReservedSegments);
            PutU32(Cp, 0x1C, OverprovisionSegments);
            PutU32(Cp, 0x20, (uint)(SegMain - NextFreeSeg));

            for (int i = 0; i < 3; i++)
            {
                PutU32(Cp, 0x24 + i * 4, (uint)Cursegs[3 + i].Segno);
                PutU16(Cp, 0x44 + i * 2, (ushort)Cursegs[3 + i].Blkoff);
                PutU32(Cp, 0x54 + i * 4, (uint)Cursegs[i].Segno);
                PutU16(Cp, 0x74 + i * 2, (ushort)Cursegs[i].Blkoff);
            }
            for (int i = 3; i < 8; i++)
            {
                PutU32(Cp, 0x24 + i * 4, 0xFFFFFFFF);
                PutU32(Cp, 0x54 + i * 4, 0xFFFFFFFF);
            }

            PutU32(Cp, 0x84, CpFlagUmount);
            PutU32(Cp, 0x88, 8);
            PutU32(Cp, 0x8C, 1);
            PutU32(Cp, 0x90, (uint)NodeBlocks);
            PutU32(Cp, 0x94, (uint)InodeCount);
            PutU32(Cp, 0x98, (uint)NextFreeNid);
            PutU32(Cp, 0x9C, (uint)SitBmBytes);
            PutU32(Cp, 0xA0, (uint)NatBmBytes);
            PutU32(Cp, 0xA4, 4092);

            PutU32(Cp, 4092, Crc32(Magic, Cp, 0, 4092));
            return Cp;
        }

        // ---- superblock ----

        private void WriteSuperblock()
        {
            var Sb = new byte[3072];
            PutU32(Sb, 0x00, Magic);
            PutU16(Sb, 0x04, 1);
            PutU16(Sb, 0x06, 16);
            PutU32(Sb, 0x08, 9);
            PutU32(Sb, 0x0C, 3);
            PutU32(Sb, 0x10, 12);
            PutU32(Sb, 0x14, 9);
            PutU32(Sb, 0x18, 1);
            PutU32(Sb, 0x1C, 1);
            PutU32(Sb, 0x20, 0);
            PutU64(Sb, 0x24, (ulong)(TotalBytes / BlockSize));
            PutU32(Sb, 0x2C, (uint)SectionCount);
            PutU32(Sb, 0x30, (uint)SegmentCount);
            PutU32(Sb, 0x34, (uint)SegCkpt);
            PutU32(Sb, 0x38, (uint)SegSit);
            PutU32(Sb, 0x3C, (uint)SegNat);
            PutU32(Sb, 0x40, (uint)SegSsa);
            PutU32(Sb, 0x44, (uint)SegMain);
            PutU32(Sb, 0x48, (uint)Segment0Blkaddr);
            PutU32(Sb, 0x4C, (uint)CpBlkaddr);
            PutU32(Sb, 0x50, (uint)SitBlkaddr);
            PutU32(Sb, 0x54, (uint)NatBlkaddr);
            PutU32(Sb, 0x58, (uint)SsaBlkaddr);
            PutU32(Sb, 0x5C, (uint)MainBlkaddr);
            PutU32(Sb, 0x60, RootIno);
            PutU32(Sb, 0x64, NodeIno);
            PutU32(Sb, 0x68, MetaIno);
            if (Compression != F2fsCompression.None)
                PutU32(Sb, 0x884, FeatureCompression | FeatureExtraAttr);
            PutU32(Sb, 0x2A0, 0);

            Array.Copy(Sb, 0, Image, 1024, Sb.Length);
            Array.Copy(Sb, 0, Image, 1024 + BlockSize, Sb.Length);
        }

        // ---- xattr ----

        private static bool SplitXattrName(string Name, out byte Index, out string Rest)
        {
            if (Name == "system.posix_acl_access") { Index = 2; Rest = ""; return true; }
            if (Name == "system.posix_acl_default") { Index = 3; Rest = ""; return true; }
            if (Name == "system.advise") { Index = 7; Rest = ""; return true; }
            if (Name.StartsWith("user.")) { Index = 1; Rest = Name.Substring(5); return true; }
            if (Name.StartsWith("trusted.")) { Index = 4; Rest = Name.Substring(8); return true; }
            if (Name.StartsWith("lustre.")) { Index = 5; Rest = Name.Substring(7); return true; }
            if (Name.StartsWith("security.")) { Index = 6; Rest = Name.Substring(9); return true; }
            if (Name.StartsWith("encryption.")) { Index = 9; Rest = Name.Substring(11); return true; }
            if (Name.StartsWith("verity.")) { Index = 11; Rest = Name.Substring(7); return true; }

            Index = 0;
            Rest = null;
            return false;
        }

        private byte[] BuildXattrBody(Node N)
        {
            var Names = new List<string>(N.Xattrs.Keys);
            Names.Sort(StringComparer.Ordinal);

            var B = new List<byte>();
            void W16(ushort v) { B.Add((byte)v); B.Add((byte)(v >> 8)); }
            void W32(uint v) { B.Add((byte)v); B.Add((byte)(v >> 8)); B.Add((byte)(v >> 16)); B.Add((byte)(v >> 24)); }

            W32(XattrMagic);
            W32(1);
            W32(0); W32(0); W32(0); W32(0);

            foreach (var Name in Names)
            {
                if (!SplitXattrName(Name, out byte Index, out string Rest))
                    throw new ArgumentException($"F2FS: unsupported xattr name '{Name}'");

                var Nb = Encoding.UTF8.GetBytes(Rest);
                var V = N.Xattrs[Name] ?? Array.Empty<byte>();
                B.Add(Index);
                B.Add((byte)Nb.Length);
                W16((ushort)V.Length);
                B.AddRange(Nb);
                B.AddRange(V);
                while ((B.Count % 4) != 0)
                    B.Add(0);
            }

            W32(0);
            return B.ToArray();
        }

        private int AddXattrs(Node N, byte[] Inode, out int ExtraBlocks)
        {
            ExtraBlocks = 0;
            if (N.Xattrs.Count == 0)
                return 0;

            var Body = BuildXattrBody(N);
            int AreaOff = InodeAddrOff + (AddrsPerInode - InlineXattrAddrs) * 4;
            int AreaSize = InlineXattrAddrs * 4;

            if (Body.Length <= AreaSize)
            {
                Inode[3] |= 0x01;                             // F2FS_INLINE_XATTR
                Array.Copy(Body, 0, Inode, AreaOff, Body.Length);
                return InlineXattrAddrs;
            }

            // External xattr node: inline flag must stay clear, otherwise the kernel
            // reads the (empty) inline area as the xattr header.
            if (Body.Length > BlockSize - 24)
                throw new ArgumentException($"F2FS: xattrs of '{N.Name}' are too large");

            uint Xnid = (uint)NextFreeNid++;
            long Xaddr = AllocNode(Xnid, N.Ino);
            var Blk = new byte[BlockSize];
            Array.Copy(Body, 0, Blk, 0, Body.Length);
            WriteNodeBlock(Xaddr, Xnid, N.Ino, Blk, 0xFFFFFFFFu);   // ofs = XATTR_NODE_OFFSET
            PutU32(Inode, InodeXattrNidOff, Xnid);
            ExtraBlocks = 1;
            return 0;
        }

        // ---- hash ----

        private static byte[] NameBytes(string Name) => Encoding.UTF8.GetBytes(Name ?? "");

        private static uint TeaHash(byte[] Name)
        {
            uint[] Buf = { 0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476 };
            int Pos = 0;
            int Len = Name.Length;

            while (true)
            {
                var In = new uint[4];
                Str2HashBuf(Name, Pos, Len, In);
                TeaTransform(Buf, In);
                Pos += 16;
                if (Len <= 16)
                    break;
                Len -= 16;
            }

            return Buf[0];
        }

        private static void Str2HashBuf(byte[] Msg, int Pos, int Len, uint[] Buf)
        {
            uint Pad = (uint)Len;
            Pad |= Pad << 8;
            Pad |= Pad << 16;

            uint Val = Pad;
            int Num = 4;
            int Bi = 0;
            int L = Len > Num * 4 ? Num * 4 : Len;

            for (int i = 0; i < L; i++)
            {
                if ((i % 4) == 0)
                    Val = Pad;
                Val = (uint)(Msg[Pos + i] + (Val << 8));
                if ((i % 4) == 3)
                {
                    Buf[Bi++] = Val;
                    Val = Pad;
                    Num--;
                }
            }

            if (--Num >= 0)
                Buf[Bi++] = Val;
            while (--Num >= 0)
                Buf[Bi++] = Pad;
        }

        private static void TeaTransform(uint[] Buf, uint[] In)
        {
            uint Sum = 0;
            uint B0 = Buf[0], B1 = Buf[1];
            uint A = In[0], B = In[1], C = In[2], D = In[3];
            int N = 16;

            do
            {
                Sum += 0x9E3779B9;
                B0 += ((B1 << 4) + A) ^ (B1 + Sum) ^ ((B1 >> 5) + B);
                B1 += ((B0 << 4) + C) ^ (B0 + Sum) ^ ((B0 >> 5) + D);
            }
            while (--N > 0);

            Buf[0] += B0;
            Buf[1] += B1;
        }

        // ---- primitives ----

        private static void PutU16(byte[] D, int O, ushort V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
        }

        private static void PutU16(byte[] D, long O, ushort V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
        }

        private static void PutU32(byte[] D, int O, uint V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
            D[O + 2] = (byte)(V >> 16);
            D[O + 3] = (byte)(V >> 24);
        }

        private static void PutU32(byte[] D, long O, uint V)
        {
            D[O] = (byte)V;
            D[O + 1] = (byte)(V >> 8);
            D[O + 2] = (byte)(V >> 16);
            D[O + 3] = (byte)(V >> 24);
        }

        private static void PutU64(byte[] D, int O, ulong V)
        {
            for (int i = 0; i < 8; i++)
                D[O + i] = (byte)(V >> (i * 8));
        }

        private static void PutU64(byte[] D, long O, ulong V)
        {
            for (int i = 0; i < 8; i++)
                D[O + i] = (byte)(V >> (i * 8));
        }

        private static uint Crc32(uint Crc, byte[] Data, int Off, int Len)
        {
            for (int i = 0; i < Len; i++)
            {
                Crc ^= Data[Off + i];
                for (int b = 0; b < 8; b++)
                    Crc = ((Crc & 1) != 0) ? ((Crc >> 1) ^ 0xEDB88320u) : (Crc >> 1);
            }
            return Crc;
        }
    }
}
