using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Types;
using FsHelper = NyaFs.Filesystem.Universal.Helper.FsHelper;

namespace NyaFs.Filesystem.UbiFs.Ubifs
{
    /// <summary>
    /// UBIFS image builder (raw volume, without the UBI container - wrap with
    /// <see cref="UbiFs.UbiFsBuilder"/>). Lays out the LEB areas
    /// (superblock / master / log / lpt / orphan / main), writes the inode, dent and data
    /// nodes of the requested tree, builds the indexing B-tree (fanout = fanout) and the
    /// LEB properties tree (LPT), then the superblock and both master nodes.
    ///
    /// Layout matches mkfs.ubifs: the index nodes occupy the last main LEB(s), one empty
    /// LEB (gc_lnum) is kept just before them, and the LPT is written as
    /// [pnodes][nnodes bottom-up..root][ltab] in the first LPT LEB.
    /// </summary>
    public class UbifsBuilder : IFilesystemBuilder
    {
        public const int DefaultMinIoSize = 2048;
        public const uint FirstIno = 16;
        public const int PadNodeSize = 28;
        public const int IdxBranchSize = 20;
        public const int IdxNodeBase = 0x1C;

        public readonly int LebSize;
        public int LebCnt;
        public readonly int MinIoSize;

        // area sizes (match the superblock fields)
        public uint LogLebs = 4;
        public uint LptLebs = 2;
        public uint OrphLebs = 1;
        public uint JheadCnt = 1;
        public uint Fanout = 8;
        public uint LsaveCnt = 256;

        /// <summary>Compression applied to regular file data nodes (and the superblock default).</summary>
        public UbifsCompression Compression = UbifsCompression.None;

        public byte[] Uuid = new byte[16];

        /// <summary>Fixed timestamp (unix seconds) written into every node.</summary>
        public ulong Time = 0;

        private uint Sqnum = 1;

        // ---- in-memory tree ----

        private class Node
        {
            public string Name;
            public FilesystemItemType Type;
            public uint User, Group, Mode;
            public byte[] Content;
            public string Target;
            public uint Major, Minor;
            public Node Parent;
            public readonly List<Node> Children = new List<Node>();
            public uint Inum;
        }

        private Node Root;

        public UbifsBuilder(int LebSize, int LebCnt, int MinIoSize = DefaultMinIoSize)
        {
            this.LebSize = LebSize;
            this.LebCnt = LebCnt;
            this.MinIoSize = MinIoSize;
        }

        // ---- IFilesystemBuilder ----

        public void Directory(string Path, uint User, uint Group, uint Mode)
        {
            if (IsRoot(Path))
            {
                if (Root == null)
                    Root = new Node { Name = "", Type = FilesystemItemType.Directory, User = User, Group = Group, Mode = Mode };
                else
                    throw new ArgumentException("Root directory already exists");
                return;
            }

            var Parent = FindParent(Path);
            if (Parent == null)
                throw new InvalidOperationException($"No parent directory for '{Path}'");

            var N = new Node { Name = FsHelper.GetName(Path), Type = FilesystemItemType.Directory, User = User, Group = Group, Mode = Mode, Parent = Parent };
            Parent.Children.Add(N);
        }

        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) =>
            AddLeaf(Path, FilesystemItemType.File, User, Group, Mode, N => N.Content = Content ?? new byte[0]);

        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) =>
            AddLeaf(Path, FilesystemItemType.SymLink, User, Group, Mode, N => N.Target = Target ?? "");

        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            AddLeaf(Path, FilesystemItemType.Character, User, Group, Mode, N => { N.Major = Major; N.Minor = Minor; });

        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            AddLeaf(Path, FilesystemItemType.Block, User, Group, Mode, N => { N.Major = Major; N.Minor = Minor; });

        public void Fifo(string Path, uint User, uint Group, uint Mode) =>
            AddLeaf(Path, FilesystemItemType.Fifo, User, Group, Mode, null);

        public void Socket(string Path, uint User, uint Group, uint Mode) =>
            AddLeaf(Path, FilesystemItemType.Socket, User, Group, Mode, null);

        // ---- path helpers ----

        private static bool IsRoot(string Path) => (Path == "/") || (Path == ".") || (Path == "") || (Path == null);

        private Node Find(string Path)
        {
            if (Root == null)
                return null;

            if (IsRoot(Path))
                return Root;

            var Cur = Root;
            foreach (var Part in Path.Trim('/').Split('/'))
            {
                if (Part.Length == 0)
                    continue;

                Node Next = null;
                foreach (var C in Cur.Children)
                    if (C.Name == Part) { Next = C; break; }

                if (Next == null)
                    return null;

                Cur = Next;
            }

            return Cur;
        }

        private Node FindParent(string Path) => Find(FsHelper.GetParentDirPath(Path));

        private void AddLeaf(string Path, FilesystemItemType Type, uint User, uint Group, uint Mode, Action<Node> Init)
        {
            var Parent = FindParent(Path);
            if (Parent == null)
                throw new InvalidOperationException($"No parent directory for '{Path}'");

            var N = new Node { Name = FsHelper.GetName(Path), Type = Type, User = User, Group = Group, Mode = Mode, Parent = Parent };
            Init?.Invoke(N);
            Parent.Children.Add(N);
        }

        // ---- node collection ----

        private class NRef
        {
            public uint W0, W1;      // simple key words
            public byte[] Data;
            public int Lnum, Offs;
        }

        private int Align8(int V) => (V + 7) & ~7;
        private int Align(int V, int A) => (V + A - 1) / A * A;

        private void AssignInums(Node N, ref uint Next)
        {
            N.Inum = (N.Parent == null) ? 1 : Next++;

            foreach (var C in N.Children)
                AssignInums(C, ref Next);
        }

        private void Emit(List<NRef> Out, uint W0, UbifsKeyType Type, uint Val, byte[] Data) =>
            Out.Add(new NRef { W0 = W0, W1 = ((uint)Type << 29) | (Val & 0x1FFFFFFF), Data = Data });

        private void EmitInode(List<NRef> Out, Node N)
        {
            uint Mode = FsHelper.GetLinuxMode(N.Type, N.Mode);
            ulong Size = 0;
            uint Nlink = 1;
            byte[] Inline = null;
            ushort Compr = 0;

            switch (N.Type)
            {
                case FilesystemItemType.Directory:
                    Size = DirSize(N);
                    Nlink = (uint)(2 + CountSubdirs(N));
                    break;

                case FilesystemItemType.File:
                    Size = (ulong)(N.Content?.Length ?? 0);
                    break;

                case FilesystemItemType.SymLink:
                    Inline = Encoding.UTF8.GetBytes(N.Target ?? "");
                    Size = (ulong)Inline.Length;
                    break;

                case FilesystemItemType.Character:
                case FilesystemItemType.Block:
                    // mkfs.ubifs stores sizeof(dev_t) (8 bytes on a 64-bit host); the kernel
                    // reads the low 32 bits via new_decode_dev().
                    Inline = BitConverter.GetBytes((ulong)MakeDev(N.Major, N.Minor));
                    break;
            }

            var Ino = UbifsNodeWriter.Inode(N.Inum, Mode, Size, N.User, N.Group, Nlink, Sqnum++, Time, Inline, Compr);
            Emit(Out, N.Inum, UbifsKeyType.Ino, 0, Ino);
        }

        private void EmitData(List<NRef> Out, Node N)
        {
            if ((N.Content == null) || (N.Content.Length == 0))
                return;

            int Block = 0;
            for (int Pos = 0; Pos < N.Content.Length; Pos += 0x1000, Block++)
            {
                int Len = Math.Min(0x1000, N.Content.Length - Pos);
                var Chunk = new byte[Len];
                Array.Copy(N.Content, Pos, Chunk, 0, Len);

                var (Stored, Compr) = Compress(Chunk);
                var D = UbifsNodeWriter.Data(N.Inum, (uint)Block, Stored, (uint)Len, Compr, Sqnum++);
                Emit(Out, N.Inum, UbifsKeyType.Data, (uint)Block, D);
            }
        }

        /// <summary>
        /// Compress one data block. Falls back to the raw bytes (compr = None) when the
        /// selected compressor is disabled or does not shrink the block, mirroring mkfs.ubifs.
        /// </summary>
        private (byte[], ushort) Compress(byte[] Data)
        {
            switch (Compression)
            {
                case UbifsCompression.Lzo:
                    {
                        var C = NyaLZO.LZO1xCompressor.Compress(Data);
                        if (C.Length < Data.Length)
                            return (C, (ushort)UbifsCompression.Lzo);
                    }
                    break;

                case UbifsCompression.Zlib:
                    {
                        var C = Deflate(Data);
                        if (C.Length < Data.Length)
                            return (C, (ushort)UbifsCompression.Zlib);
                    }
                    break;

                case UbifsCompression.Zstd:
                    {
                        var C = ImageFormat.Compressors.ZStd.CompressWithHeader(Data);
                        if (C.Length < Data.Length)
                            return (C, (ushort)UbifsCompression.Zstd);
                    }
                    break;
            }

            return (Data, (ushort)UbifsCompression.None);
        }

        /// <summary>UBIFS zlib = raw deflate stream (no zlib/gzip wrapper).</summary>
        private static byte[] Deflate(byte[] Data)
        {
            using (var Output = new System.IO.MemoryStream())
            {
                using (var Deflate = new System.IO.Compression.DeflateStream(Output, System.IO.Compression.CompressionLevel.Optimal, true))
                    Deflate.Write(Data, 0, Data.Length);

                return Output.ToArray();
            }
        }

        private void EmitDent(List<NRef> Out, Node Dir, Node Child)
        {
            uint Hash = UbifsNodeWriter.R5Hash(Child.Name);
            var D = UbifsNodeWriter.Dent(Dir.Inum, Child.Inum, DentType(Child.Type), Hash, Child.Name, Sqnum++);
            Emit(Out, Dir.Inum, UbifsKeyType.Dent, Hash, D);
        }

        private void Collect(List<NRef> Out, Node N)
        {
            EmitInode(Out, N);

            if (N.Type == FilesystemItemType.File)
                EmitData(Out, N);

            foreach (var C in N.Children)
                EmitDent(Out, N, C);

            foreach (var C in N.Children)
                Collect(Out, C);
        }

        private ulong DirSize(Node N)
        {
            ulong Res = 0;
            foreach (var C in N.Children)
                Res += (ulong)Align8(0x38 + Encoding.UTF8.GetByteCount(C.Name) + 1);
            return Res;
        }

        private static int CountSubdirs(Node N)
        {
            int Res = 0;
            foreach (var C in N.Children)
                if (C.Type == FilesystemItemType.Directory)
                    Res++;
            return Res;
        }

        /// <summary>UBIFS on-flash directory entry type (enum ubifs_dent_node_type).</summary>
        private static byte DentType(FilesystemItemType T) => T switch
        {
            FilesystemItemType.Directory => 1,
            FilesystemItemType.SymLink => 2,
            FilesystemItemType.Block => 3,
            FilesystemItemType.Character => 4,
            FilesystemItemType.Fifo => 5,
            FilesystemItemType.Socket => 6,
            _ => 0
        };

        /// <summary>Linux glibc makedev encoding.</summary>
        private static uint MakeDev(uint Major, uint Minor) =>
            ((Major & 0xFFF) << 8) | (Minor & 0xFF) | ((Minor & 0xFFFFF00) << 12);

        // ---- image build ----

        public byte[] GetFilesystemImage()
        {
            if (Root == null)
                throw new InvalidOperationException("No root directory: call Directory(\"/\", ...) first");

            // 1. assign inode numbers and generate the real nodes
            uint Next = FirstIno;
            AssignInums(Root, ref Next);
            uint HighestInum = Next - 1;

            var Real = new List<NRef>();
            Collect(Real, Root);

            Real.Sort((A, B) => (A.W0 != B.W0) ? A.W0.CompareTo(B.W0) : A.W1.CompareTo(B.W1));

            // 2. compute the index tree node sizes and how many index LEBs are needed
            uint MainFirst = 1 + 2 + LogLebs + LptLebs + OrphLebs;
            uint LptFirst = 1 + 2 + LogLebs;
            int IndexLebs = CountIndexLebs(Real.Count);
            int RealLebs = CountRealLebs(Real);

            int Required = (int)MainFirst + RealLebs + 1 + IndexLebs;

            // kernel validate_sb: min_leb_cnt = SB + MST + log + lpt + orph + (jhead + NONDATA_JHEADS_CNT) + 6
            int MinLebCnt = 1 + 2 + (int)LogLebs + (int)LptLebs + (int)OrphLebs + ((int)JheadCnt + 2) + 6;
            if (Required < MinLebCnt)
                Required = MinLebCnt;

            if (LebCnt < Required)
                LebCnt = Required;

            int IndexFirst = LebCnt - IndexLebs;
            int GcLnum = IndexFirst - 1;

            var Lebs = new byte[LebCnt][];
            for (int i = 0; i < LebCnt; i++)
            {
                Lebs[i] = new byte[LebSize];
                for (int j = 0; j < LebSize; j++)
                    Lebs[i][j] = 0xFF;
            }

            var Ends = new long[LebCnt];       // end of the last real/index node in the LEB (0 = empty)

            // 3. place the real nodes into the main area
            PackNodes(Lebs, Real, (int)MainFirst, Ends);

            // 4. build and place the index tree into the last main LEB(s)
            var IndexSizes = IndexNodeSizes(Real.Count);
            int idxFirst = IndexFirst;
            int idxOff = 0;
            var Cur = Real;
            int Level = 0;
            NRef RootIdx = null;

            while (true)
            {
                var NextLevel = new List<NRef>();

                for (int Start = 0; Start < Cur.Count; Start += (int)Fanout)
                {
                    int Cnt = Math.Min((int)Fanout, Cur.Count - Start);
                    var Branches = new List<(uint, uint, uint, uint, UbifsKeyType, uint)>(Cnt);

                    for (int i = 0; i < Cnt; i++)
                    {
                        var C = Cur[Start + i];
                        Branches.Add(((uint)C.Lnum, (uint)C.Offs, (uint)C.Data.Length, C.W0, (UbifsKeyType)(C.W1 >> 29), C.W1 & 0x1FFFFFFF));
                    }

                    var Node = UbifsNodeWriter.Index(Level, Branches, Sqnum++);

                    int Len = Align8(Node.Length);
                    if ((idxOff != 0) && (idxOff + Len > LebSize))
                    {
                        if (LebSize - idxOff >= PadNodeSize)
                            WritePad(Lebs, idxFirst, idxOff);
                        idxFirst++;
                        idxOff = 0;
                    }

                    Array.Copy(Node, 0, Lebs[idxFirst], idxOff, Node.Length);

                    var E = new NRef { W0 = Cur[Start].W0, W1 = Cur[Start].W1, Data = Node, Lnum = idxFirst, Offs = idxOff };
                    NextLevel.Add(E);

                    idxOff += Len;
                    Ends[idxFirst] = idxOff;
                }

                Cur = NextLevel;
                Level++;

                if (Cur.Count == 1)
                {
                    RootIdx = Cur[0];
                    break;
                }
            }

            if (idxOff > 0 && LebSize - idxOff >= PadNodeSize)
                WritePad(Lebs, idxFirst, idxOff);

            int IndexEndOff = RootIdx.Offs + RootIdx.Data.Length;
            int IheadOffs = Align(IndexEndOff, MinIoSize);

            ulong IndexSize = 0;
            foreach (var Sz in IndexSizes)
                IndexSize += (ulong)Align8(Sz);

            // 5. superblock / master / LPT
            var Props = ComputeLprops(Lebs, Ends, (int)MainFirst, IndexFirst, IndexLebs);

            var Lpt = BuildLpt(Lebs, (int)LptFirst, (int)MainFirst, Props);

            int MainLebs = LebCnt - (int)MainFirst;

            var Sb = UbifsNodeWriter.Superblock((uint)MinIoSize, (uint)LebSize, (uint)LebCnt, (uint)LebCnt,
                (ulong)LebSize * (ulong)Math.Min(15, MainLebs), LogLebs, LptLebs, OrphLebs, JheadCnt, Fanout, LsaveCnt,
                (ushort)Compression, Uuid, Sqnum++);
            Array.Copy(Sb, 0, Lebs[0], 0, Sb.Length);

            // log head: a commit start node (the log is otherwise empty)
            var Cs = UbifsNodeWriter.Cs(Sqnum++, 0);
            Array.Copy(Cs, 0, Lebs[3], 0, Cs.Length);

            ulong TotalFree = 0, TotalDirty = 0, TotalUsed = 0, TotalDead = 0;
            foreach (var P in Props)
            {
                TotalFree += (ulong)P.Free;
                TotalDirty += (ulong)P.Dirty;
                if (!P.Index)
                {
                    TotalUsed += (ulong)P.Used;
                    if (P.Free == 0)
                        TotalDead += (ulong)(LebSize - P.Used);
                }
            }

            int EmptyLebs = 0;
            for (int i = (int)MainFirst; i < LebCnt; i++)
                if (i < IndexFirst && Ends[i] == 0)
                    EmptyLebs++;

            var Mst = UbifsNodeWriter.Master(HighestInum, 0, 2 /*NO_ORPHS*/, 3,
                (uint)RootIdx.Lnum, (uint)RootIdx.Offs, (uint)RootIdx.Data.Length, (uint)GcLnum,
                (uint)RootIdx.Lnum, (uint)IheadOffs, IndexSize,
                TotalFree, TotalDirty, TotalUsed, TotalDead, 0,
                (uint)LptFirst, (uint)Lpt.LptOffs,
                (uint)LptFirst, (uint)Lpt.NheadOffs,
                (uint)LptFirst, (uint)Lpt.LtabOffs,
                0xFFFFFFFF, 0xFFFFFFFF,
                (uint)MainFirst,
                (uint)EmptyLebs, (uint)IndexLebs, (uint)LebCnt, Sqnum++);

            Array.Copy(Mst, 0, Lebs[1], 0, Mst.Length);
            Array.Copy(Mst, 0, Lebs[2], 0, Mst.Length);

            var Pad = UbifsNodeWriter.Pad((uint)(2048 - Mst.Length - PadNodeSize), Sqnum++);
            Array.Copy(Pad, 0, Lebs[1], Mst.Length, Pad.Length);
            Array.Copy(Pad, 0, Lebs[2], Mst.Length, Pad.Length);

            // flatten
            var Res = new byte[(long)LebCnt * LebSize];
            for (int i = 0; i < LebCnt; i++)
                Array.Copy(Lebs[i], 0, Res, (long)i * LebSize, LebSize);

            return Res;
        }

        // ---- main-area packing ----

        private int CountRealLebs(List<NRef> Real)
        {
            int Lnum = 0, Off = 0;
            foreach (var N in Real)
            {
                int Len = Align8(N.Data.Length);
                if (Off + Len > LebSize) { Lnum++; Off = 0; }
                Off += Len;
            }
            return Lnum + 1;
        }

        private void PackNodes(byte[][] Lebs, List<NRef> Nodes, int First, long[] Ends)
        {
            int Lnum = First, Off = 0;
            foreach (var N in Nodes)
            {
                int Len = Align8(N.Data.Length);
                if (Off + Len > LebSize)
                {
                    if (LebSize - Off >= PadNodeSize)
                        WritePad(Lebs, Lnum, Off);
                    Lnum++;
                    Off = 0;
                }

                N.Lnum = Lnum;
                N.Offs = Off;
                Array.Copy(N.Data, 0, Lebs[Lnum], Off, N.Data.Length);
                Off += Len;
                Ends[Lnum] = Off;
            }

            if (Off > 0 && LebSize - Off >= PadNodeSize)
                WritePad(Lebs, Lnum, Off);
        }

        private static void WritePad(byte[][] Lebs, int Lnum, int Off)
        {
            var Pad = UbifsNodeWriter.Pad((uint)(Lebs[Lnum].Length - Off - PadNodeSize), 1);
            Array.Copy(Pad, 0, Lebs[Lnum], Off, Pad.Length);
        }

        // ---- index tree sizing ----

        private static int IdxNodeSize(int Children) => IdxNodeBase + Children * IdxBranchSize;

        private List<int> IndexNodeSizes(int RealCnt)
        {
            var Res = new List<int>();
            int Cnt = RealCnt;

            while (true)
            {
                int Full = Cnt / (int)Fanout;
                int Rem = Cnt % (int)Fanout;

                for (int i = 0; i < Full; i++)
                    Res.Add(IdxNodeSize((int)Fanout));
                if (Rem > 0)
                    Res.Add(IdxNodeSize(Rem));

                int Nodes = Full + (Rem > 0 ? 1 : 0);
                if (Nodes <= 1)
                    break;

                Cnt = Nodes;
            }

            return Res;
        }

        private int CountIndexLebs(int RealCnt)
        {
            int Lebs = 1, Off = 0;
            foreach (var Sz in IndexNodeSizes(RealCnt))
            {
                int Len = Align8(Sz);
                if (Off + Len > LebSize) { Lebs++; Off = 0; }
                Off += Len;
            }
            return Lebs;
        }

        // ---- LPT ----

        private struct Lp { public int Free; public int Dirty; public int Used; public bool Index; }

        private List<Lp> ComputeLprops(byte[][] Lebs, long[] Ends, int MainFirst, int IndexFirst, int IndexLebs)
        {
            var Res = new List<Lp>();
            for (int Lnum = MainFirst; Lnum < LebCnt; Lnum++)
            {
                bool Idx = (Lnum >= IndexFirst) && (Lnum < IndexFirst + IndexLebs);
                long Used = Ends[Lnum];
                int Free, Dirty;

                if (Used == 0)
                {
                    Free = LebSize;
                    Dirty = 0;
                }
                else
                {
                    int A = Align((int)Used + (LebSize - (int)Used >= PadNodeSize ? PadNodeSize : 0), MinIoSize);
                    Free = LebSize - A;
                    Dirty = A - (int)Used;
                }

                Res.Add(new Lp { Free = Free, Dirty = Dirty, Used = (int)Used, Index = Idx });
            }
            return Res;
        }

        private class LptResult
        {
            public int LptOffs;
            public int NheadOffs;
            public int LtabOffs;
        }

        private LptResult BuildLpt(byte[][] Lebs, int LptFirst, int MainFirst, List<Lp> Props)
        {
            int MainLebs = LebCnt - MainFirst;
            var G = UbifsLpt.Geometry.Calc(LebSize, LebCnt, LebCnt, MainLebs, MainFirst, (int)LptLebs, LptFirst, false);

            int PnodeCnt = G.PnodeCnt;
            int Height = G.LptHght;

            // node counts/offsets per nnode level (1 = directly above pnodes, Height = root)
            var LevelCnt = new int[Height + 1];
            var LevelOff = new int[Height + 1];
            int Off = PnodeCnt * G.PnodeSz;
            int Prev = PnodeCnt;

            for (int L = 1; L <= Height; L++)
            {
                int Cnt = UbifsLpt.DivRoundUp(Prev, UbifsLpt.FANOUT);
                LevelCnt[L] = Cnt;
                LevelOff[L] = Off;
                Off += Cnt * G.NnodeSz;
                Prev = Cnt;
            }

            if (Off + G.LtabSz > LebSize)
                throw new InvalidOperationException("UBIFS LPT does not fit into a single LPT LEB");

            var Area = Lebs[LptFirst];

            // pnodes
            var PnodeOff = new int[PnodeCnt];
            for (int p = 0; p < PnodeCnt; p++)
            {
                PnodeOff[p] = p * G.PnodeSz;
                var Group = new UbifsLpt.Lprops[UbifsLpt.FANOUT];
                for (int i = 0; i < UbifsLpt.FANOUT; i++)
                {
                    int idx = p * UbifsLpt.FANOUT + i;
                    if (idx < Props.Count)
                        Group[i] = new UbifsLpt.Lprops { Free = Props[idx].Free, Dirty = Props[idx].Dirty, Flags = Props[idx].Index ? UbifsLpt.LPROPS_INDEX : 0 };
                    else
                        Group[i] = new UbifsLpt.Lprops { Free = LebSize, Dirty = 0, Flags = 0 };
                }
                var Node = UbifsLpt.PackPnode(G, p, Group);
                Array.Copy(Node, 0, Area, PnodeOff[p], Node.Length);
            }

            // nnodes, bottom-up levels, root is the single node of the last level
            for (int L = 1; L <= Height; L++)
            {
                int Cnt = LevelCnt[L];
                int ChildBase = (L == 1) ? 0 : LevelOff[L - 1];
                int ChildCnt = (L == 1) ? PnodeCnt : LevelCnt[L - 1];
                int ChildSz = (L == 1) ? G.PnodeSz : G.NnodeSz;

                for (int k = 0; k < Cnt; k++)
                {
                    var Br = new (int, int)[UbifsLpt.FANOUT];
                    for (int i = 0; i < UbifsLpt.FANOUT; i++)
                    {
                        int c = k * UbifsLpt.FANOUT + i;
                        Br[i] = (c < ChildCnt) ? (LptFirst, ChildBase + c * ChildSz) : (0, 0);
                    }

                    var Node = UbifsLpt.PackNnode(G, k, Br);
                    Array.Copy(Node, 0, Area, LevelOff[L] + k * G.NnodeSz, Node.Length);
                }
            }

            int RootOff = LevelOff[Height];

            // ltab: properties of the LPT LEBs themselves
            int LtabOff = RootOff + G.NnodeSz;
            int Used = LtabOff + G.LtabSz;
            var Ltab = new (int Free, int Dirty)[(int)LptLebs];
            int LFree = LebSize - Align(Used, MinIoSize);
            Ltab[0] = (LFree, Align(Used, MinIoSize) - Used);
            for (int i = 1; i < (int)LptLebs; i++)
                Ltab[i] = (LebSize, 0);

            var LtabBytes = UbifsLpt.PackLtab(G, Ltab);
            Array.Copy(LtabBytes, 0, Area, LtabOff, LtabBytes.Length);

            return new LptResult { LptOffs = RootOff, NheadOffs = Align(Used, MinIoSize), LtabOffs = LtabOff };
        }
    }
}
