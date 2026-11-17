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
    public class UbifsReader : IFilesystemReader, IRootFsInfo, IRootXattrs
    {
        public readonly UbiFsReader Ubi;
        public readonly UbiVolume Volume;
        private readonly byte[] RawSource;
        private readonly int RawLebSize;

        public byte[] Superblock;
        public byte[] Master;

        /// <summary>Non-fatal problems found while reading (torn branches are skipped).</summary>
        public readonly List<string> Problems = new List<string>();

        /// <summary>True when the on-flash index could not be read completely.</summary>
        public bool Degraded { get; private set; }

        /// <summary>A master node candidate: its location and the root index node it points to.</summary>
        private class MasterNode
        {
            public int Leb;
            public int Off;
            public ulong Sqnum;
            public uint RootLnum;
            public uint RootOffs;
        }


        public readonly List<UbifsNode> Inodes = new List<UbifsNode>();
        public readonly List<UbifsNode> Dents = new List<UbifsNode>();
        public readonly List<UbifsNode> Xents = new List<UbifsNode>();
        public readonly List<UbifsNode> Datas = new List<UbifsNode>();

        private readonly Dictionary<uint, byte[]> LebCache = new Dictionary<uint, byte[]>();
        private readonly Dictionary<uint, UbifsNode> InodeByInum = new Dictionary<uint, UbifsNode>();
        private readonly Dictionary<uint, List<UbifsNode>> DentsByParent = new Dictionary<uint, List<UbifsNode>>();
        private readonly Dictionary<uint, List<UbifsNode>> XentsByHost = new Dictionary<uint, List<UbifsNode>>();
        private readonly Dictionary<uint, List<UbifsNode>> DataByInum = new Dictionary<uint, List<UbifsNode>>();

        public const uint RootInum = 1;

        public UbifsReader(UbiFsReader Ubi, UbiVolume Volume)
        {
            this.Ubi = Ubi;
            this.Volume = Volume;
            Init();
        }

        public UbifsReader(byte[] Raw, int LebSize)
        {
            RawSource = Raw;
            RawLebSize = LebSize;
            Init();
        }

        public int LebSize => (RawSource != null) ? RawLebSize : Ubi.LebSize;

        private byte[] Leb(uint Lnum)
        {
            if (!LebCache.TryGetValue(Lnum, out var D))
            {
                if (RawSource != null)
                {
                    var Offset = (long)Lnum * RawLebSize;
                    if ((Offset < 0) || (Offset + RawLebSize > RawSource.Length))
                        throw new InvalidOperationException("UBIFS raw LEB " + Lnum + " is out of range");
                    D = new byte[RawLebSize];
                    Array.Copy(RawSource, Offset, D, 0, RawLebSize);
                }
                else
                    D = Ubi.ReadLeb(Volume.Id, Lnum);
                LebCache[Lnum] = D;
            }
            return D;
        }

        private byte[] SafeLeb(uint Lnum)
        {
            try { return Leb(Lnum); }
            catch { return null; }
        }

        private void ResetCollections()
        {
            Inodes.Clear();
            Dents.Clear();
            Xents.Clear();
            Datas.Clear();
            InodeByInum.Clear();
            DentsByParent.Clear();
            XentsByHost.Clear();
            DataByInum.Clear();
        }

        /// <summary>
        /// Find every valid master node. UBIFS keeps one master copy per commit; some vendor
        /// images (e.g. Amlogic) append them page by page into LEB 1 instead of rewriting a
        /// single slot, so the newest version has the highest sqnum, not the first one.
        /// </summary>
        private List<MasterNode> FindMasters()
        {
            var Result = new List<MasterNode>();

            foreach (var LNum in new uint[] { 1, 2, 0 })
            {
                var D = SafeLeb(LNum);
                if (D == null)
                    continue;

                for (int Off = 0; Off + UbifsNode.HeaderSize <= D.Length; Off += 8)
                {
                    if (!UbifsNode.IsValidNode(D, Off, UbifsNodeType.Mst))
                        continue;

                    Result.Add(new MasterNode
                    {
                        Leb = (int)LNum,
                        Off = Off,
                        Sqnum = BitConverter.ToUInt64(D, Off + 0x08),
                        RootLnum = BitConverter.ToUInt32(D, Off + 0x30),
                        RootOffs = BitConverter.ToUInt32(D, Off + 0x34)
                    });
                }
            }

            Result.Sort((A, B) => B.Sqnum.CompareTo(A.Sqnum));
            return Result;
        }

        private void Init()
        {
            Superblock = SafeLeb(0);
            if ((Superblock == null) || !UbifsNode.IsValidMagic(Superblock, 0) ||
                ((UbifsNodeType)Superblock[0x14] != UbifsNodeType.Sb))
                throw new InvalidOperationException("UBIFS: invalid superblock node in LEB 0");

            var Masters = FindMasters();
            if (Masters.Count == 0)
                throw new InvalidOperationException("UBIFS: no valid master node found");

            MasterNode Chosen = null;
            var ChosenScore = -1;
            var TriedRoots = new HashSet<(uint, uint)>();

            foreach (var M in Masters)
            {
                // Consecutive master versions usually point to the same unchanged root.
                if (!TriedRoots.Add((M.RootLnum, M.RootOffs)))
                    continue;

                ResetCollections();
                var Current = new List<string>();
                var Complete = TryTraverse(M, Current);

                if ((Inodes.Count == 0) && (Current.Count == 0))
                    continue;

                var Score = Complete ? int.MaxValue : Inodes.Count;
                if (Score > ChosenScore)
                {
                    Chosen = M;
                    ChosenScore = Score;
                }

                if (Complete)
                    break;
            }

            if (Chosen == null)
                throw new InvalidOperationException("UBIFS: no readable index tree found");

            // The collections currently hold the last attempted master; rebuild for the chosen one.
            ResetCollections();
            var Final = new List<string>();
            var FinalComplete = TryTraverse(Chosen, Final);

            Master = SafeLeb((uint)Chosen.Leb);
            Problems.AddRange(Final);
            Degraded = !FinalComplete;

            BuildIndexes();

            // A torn index often still yields a few nodes, so "complete" alone is not enough:
            // also rescan when most directory entries point at inodes the tree never reached.
            var NeedScan = !FinalComplete;
            if (!NeedScan && (Dents.Count > 0))
            {
                var Missing = 0;
                foreach (var D in Dents)
                {
                    if (!InodeByInum.ContainsKey((uint)DentInum(D)))
                        Missing++;
                }

                NeedScan = (Missing * 4 >= Dents.Count);
            }

            var Rebuilt = false;
            if (NeedScan)
            {
                var Scan = new ScanResult();
                ScanLeaves(Scan);

                if ((Scan.Inodes.Count > 0) && (Scan.Inodes.Count >= Inodes.Count))
                {
                    Inodes.Clear(); Inodes.AddRange(Scan.Inodes);
                    Dents.Clear(); Dents.AddRange(Scan.Dents);
                    Xents.Clear(); Xents.AddRange(Scan.Xents);
                    Datas.Clear(); Datas.AddRange(Scan.Datas);
                    Rebuilt = true;
                    Degraded = true;

                    BuildIndexes();
                }
            }

            if (Inodes.Count == 0)
                throw new InvalidOperationException("UBIFS: no inodes found");

            if (Rebuilt)
                Log.Warning(1, $"UBIFS: index is damaged (master LEB {Chosen.Leb}@{Chosen.Off:x}, sqnum {Chosen.Sqnum}); reconstructed {Inodes.Count} inodes from a full flash scan (deletions are not replayed)");
            else if (Degraded)
                Log.Warning(1, $"UBIFS: index is damaged (master LEB {Chosen.Leb}@{Chosen.Off:x}, sqnum {Chosen.Sqnum}); read {Inodes.Count} inodes, skipped {Final.Count} broken path(s)");
        }

        private bool TryTraverse(MasterNode Master, List<string> Problems)
        {
            try
            {
                return TraverseIndex(Master.RootLnum, Master.RootOffs, 0, Problems, new HashSet<(uint, int)>());
            }
            catch (Exception E)
            {
                AddProblem(Problems, E.Message);
                return false;
            }
        }

        /// <summary>
        /// Walk the index tree. Nodes are validated (magic, length, CRC) and resolved by key,
        /// so a torn or vendor-padded node is reported instead of crashing the reader.
        /// Returns true when the whole tree below this node was readable.
        /// </summary>
        private bool TraverseIndex(uint Lnum, uint Offs, int Depth, List<string> Problems, HashSet<(uint, int)> Visited)
        {
            if (Depth > 64)
            {
                AddProblem(Problems, "index tree is too deep");
                return false;
            }

            var Off = ResolveIndexNode(Lnum, Offs, out var D);
            if (Off < 0)
            {
                AddProblem(Problems, $"no valid index node at LEB {Lnum}+{Offs:x}");
                return false;
            }

            if (!Visited.Add((Lnum, Off)))
                return true;

            if (Off != (int)Offs)
                AddProblem(Problems, $"index node at LEB {Lnum}+{Offs:x} resolved to +{Off:x}");

            var Node = new UbifsIndexNode(D, Off, Lnum);

            var BranchStart = Off + 0x1C;
            var MaxBranches = ((int)Node.Len - 0x1C) / UbifsIndexNode.BranchSize;
            if ((Node.ChildCnt > MaxBranches) ||
                (BranchStart + Node.ChildCnt * UbifsIndexNode.BranchSize > D.Length))
            {
                AddProblem(Problems, $"index node at LEB {Lnum}+{Off:x} has invalid child count {Node.ChildCnt}");
                return false;
            }

            var ExpectIndex = Node.Level > 0;
            var ExpectedLevel = Node.Level - 1;
            var Complete = true;

            for (int i = 0; i < Node.ChildCnt; i++)
            {
                Node.GetBranch(i, out uint BLnum, out uint BOffs, out _, out var BKey);
                var Key = KeyValue(BKey);

                var BOff = ResolveTarget(BLnum, BOffs, Key, ExpectedLevel, out var BD);
                if (BOff < 0)
                {
                    AddProblem(Problems, $"branch {i} of LEB {Lnum}+{Off:x} has no {DescribeNode(ExpectIndex)} with key {BKey} in LEB {BLnum}");
                    Complete = false;
                    continue;
                }

                if (BOff != (int)BOffs)
                    AddProblem(Problems, $"node at LEB {BLnum}+{BOffs:x} resolved to +{BOff:x}");

                var Target = new UbifsNode(BD, BOff, BLnum, true);

                if (Target.Type == UbifsNodeType.Idx)
                    Complete &= TraverseIndex(BLnum, (uint)BOff, Depth + 1, Problems, Visited);
                else if (!Collect(Target))
                {
                    AddProblem(Problems, $"branch {i} of LEB {Lnum}+{Off:x} points to unsupported node type {Target.Type}");
                    Complete = false;
                }
            }

            return Complete;
        }

        private const int MaxSkew = 0x1000;
        private const int MaxProblems = 200;

        private static void AddProblem(List<string> Problems, string Text)
        {
            if (Problems.Count < MaxProblems)
                Problems.Add(Text);
        }

        private static ulong KeyValue(UbifsKey Key) => ((ulong)Key.W1 << 32) | Key.Inum;

        private static string DescribeNode(bool Index) => Index ? "index node" : "leaf";

        /// <summary>
        /// Resolve the root index node. The master node has no key to match, so fall back to
        /// the first valid index node at or after the recorded offset.
        /// </summary>
        private int ResolveIndexNode(uint Lnum, uint Offs, out byte[] D)
        {
            D = SafeLeb(Lnum);
            if (D == null)
                return -1;

            int Expected = (int)Offs;
            if (UbifsNode.IsValidNode(D, Expected, UbifsNodeType.Idx))
                return Expected;

            var Map = GetNodeMap(Lnum);
            return Map.NextAnyIndex(Expected, MaxSkew);
        }

        /// <summary>
        /// Resolve a branch target. The recorded offset is used when the node there is valid
        /// and its key matches; otherwise the node is looked up by key inside the LEB, which
        /// is unambiguous and tolerates the padding some vendor writers insert between nodes.
        /// </summary>
        private int ResolveTarget(uint Lnum, uint Offs, ulong Key, int Level, out byte[] D)
        {
            D = SafeLeb(Lnum);
            if (D == null)
                return -1;

            var ExpectIndex = Level > 0;
            int Expected = (int)Offs;

            if (UbifsNode.IsValidNode(D, Expected) && NodeHasKey(D, Expected, Key, Level))
                return Expected;

            var Map = GetNodeMap(Lnum);

            if (ExpectIndex)
                return Map.FindIndexByKey(Key, Level, Expected, MaxSkew);

            return Map.FindLeafByKey(Key);
        }

        private static bool NodeHasKey(byte[] D, int Off, ulong Key, int Level)
        {
            var Type = (UbifsNodeType)D[Off + 0x14];

            if (Type == UbifsNodeType.Idx)
            {
                if (BitConverter.ToUInt16(D, Off + 0x1A) != Level)
                    return false;

                var ChildCnt = BitConverter.ToUInt16(D, Off + 0x18);
                return (ChildCnt > 0) && (Off + 0x1C + 0x14 <= D.Length) &&
                    (BitConverter.ToUInt64(D, Off + 0x1C + 12) == Key);
            }

            return (Level <= 0) && (Off + 32 <= D.Length) &&
                (BitConverter.ToUInt64(D, Off + 24) == Key);
        }

        /// <summary>Valid nodes of one LEB, indexed by offset and by key.</summary>
        private class LebNodeMap
        {
            public readonly Dictionary<int, List<int>> IndexOffsets = new Dictionary<int, List<int>>();

            // A key may exist in several generations (data nodes rewritten in place, index
            // nodes along the leftmost path share the same first key); leaves keep the newest.
            public readonly Dictionary<ulong, (int Off, ulong Sqnum)> Leaves =
                new Dictionary<ulong, (int, ulong)>();
            public readonly Dictionary<ulong, List<(int Off, ulong Sqnum, int Level)>> Indexes =
                new Dictionary<ulong, List<(int, ulong, int)>>();

            public int FindLeafByKey(ulong Key) =>
                Leaves.TryGetValue(Key, out var L) ? L.Off : -1;

            /// <summary>
            /// Index nodes that start with the same key are told apart by tree level and by
            /// the recorded offset (the smallest key propagates up the whole left spine).
            /// </summary>
            public int FindIndexByKey(ulong Key, int Level, int Expected, int MaxSkew)
            {
                if (!Indexes.TryGetValue(Key, out var Candidates))
                    return -1;

                var Best = -1;
                var BestDistance = int.MaxValue;

                foreach (var C in Candidates)
                {
                    if (C.Level != Level)
                        continue;

                    var Distance = Math.Abs(C.Off - Expected);
                    if ((Distance <= MaxSkew) && (Distance < BestDistance))
                    {
                        Best = C.Off;
                        BestDistance = Distance;
                    }
                }

                return Best;
            }

            public int NextAnyIndex(int From, int MaxSkew)
            {
                var Best = -1;

                foreach (var Offsets in IndexOffsets.Values)
                {
                    var Candidate = Next(Offsets, From, MaxSkew);
                    if ((Candidate >= 0) && ((Best < 0) || (Candidate < Best)))
                        Best = Candidate;
                }

                return Best;
            }

            private static int Next(List<int> Offsets, int From, int MaxSkew)
            {
                var Low = 0;
                var High = Offsets.Count - 1;
                var Result = -1;

                while (Low <= High)
                {
                    var Mid = (Low + High) / 2;
                    if (Offsets[Mid] >= From)
                    {
                        Result = Offsets[Mid];
                        High = Mid - 1;
                    }
                    else
                        Low = Mid + 1;
                }

                if ((Result >= 0) && (Result - From > MaxSkew))
                    return -1;

                return Result;
            }

            public void Add(byte[] D, int Off, UbifsNodeType Type, ulong Sqnum)
            {
                if (Type == UbifsNodeType.Idx)
                {
                    var Level = BitConverter.ToUInt16(D, Off + 0x1A);

                    if (!IndexOffsets.TryGetValue(Level, out var Offsets))
                        IndexOffsets[Level] = Offsets = new List<int>();
                    Offsets.Add(Off);

                    var ChildCnt = BitConverter.ToUInt16(D, Off + 0x18);
                    if ((ChildCnt > 0) && (Off + 0x1C + 0x14 <= D.Length))
                    {
                        var Key = BitConverter.ToUInt64(D, Off + 0x1C + 12);
                        if (!Indexes.TryGetValue(Key, out var List))
                            Indexes[Key] = List = new List<(int, ulong, int)>();

                        List.Add((Off, Sqnum, Level));
                    }
                }
                else if ((Type == UbifsNodeType.Ino) || (Type == UbifsNodeType.Data) ||
                         (Type == UbifsNodeType.Dent) || (Type == UbifsNodeType.Xent))
                {
                    if (Off + 32 <= D.Length)
                        AddLeaf(BitConverter.ToUInt64(D, Off + 24), Off, Sqnum);
                }
            }

            private void AddLeaf(ulong Key, int Off, ulong Sqnum)
            {
                if (!Leaves.TryGetValue(Key, out var Current) || (Sqnum > Current.Sqnum))
                    Leaves[Key] = (Off, Sqnum);
            }
        }

        private readonly Dictionary<uint, LebNodeMap> NodeMaps = new Dictionary<uint, LebNodeMap>();

        private class ScanResult
        {
            public readonly List<UbifsNode> Inodes = new List<UbifsNode>();
            public readonly List<UbifsNode> Dents = new List<UbifsNode>();
            public readonly List<UbifsNode> Xents = new List<UbifsNode>();
            public readonly List<UbifsNode> Datas = new List<UbifsNode>();
        }

        /// <summary>
        /// Rebuild the filesystem from a full flash scan. The index only *locates* leaves;
        /// when it is torn (power loss, non-standard vendor writer) the leaves are still on
        /// flash and the newest version of every key wins - the same rule UBIFS recovery
        /// applies. Deletions are not replayed, so removed files may reappear.
        /// </summary>
        private void ScanLeaves(ScanResult Result)
        {
            var Best = new Dictionary<ulong, (ulong Sqnum, UbifsNode Node)>();
            var Named = new Dictionary<(uint Parent, string Name), (ulong Sqnum, UbifsNode Node)>();

            foreach (var LNum in MappedLebs())
            {
                var D = SafeLeb(LNum);
                if (D == null)
                    continue;

                for (int Off = 0; Off + UbifsNode.HeaderSize <= D.Length; Off += 8)
                {
                    if (!UbifsNode.IsValidNode(D, Off))
                        continue;

                    var Type = (UbifsNodeType)D[Off + 0x14];
                    if ((Type != UbifsNodeType.Ino) && (Type != UbifsNodeType.Data) &&
                        (Type != UbifsNodeType.Dent) && (Type != UbifsNodeType.Xent))
                        continue;

                    var Node = new UbifsNode(D, Off, LNum, true);
                    var Sqnum = BitConverter.ToUInt64(D, Off + 0x08);

                    if ((Type == UbifsNodeType.Dent) || (Type == UbifsNodeType.Xent))
                    {
                        // Different filesystem generations may hash names differently, so
                        // deduplicate directory entries by (parent, name) rather than by key.
                        var NamedKey = (Node.Key.Inum, DentNameAt(D, Off));
                        if (!Named.TryGetValue(NamedKey, out var Current) || (Sqnum > Current.Sqnum))
                            Named[NamedKey] = (Sqnum, Node);
                        continue;
                    }

                    var Key = KeyValue(Node.Key);
                    if (!Best.TryGetValue(Key, out var Prev) || (Sqnum > Prev.Sqnum))
                        Best[Key] = (Sqnum, Node);
                }
            }

            foreach (var Entry in Best.Values)
            {
                switch (Entry.Node.Type)
                {
                    case UbifsNodeType.Ino: Result.Inodes.Add(Entry.Node); break;
                    case UbifsNodeType.Data: Result.Datas.Add(Entry.Node); break;
                }
            }

            foreach (var Entry in Named.Values)
            {
                switch (Entry.Node.Type)
                {
                    case UbifsNodeType.Dent: Result.Dents.Add(Entry.Node); break;
                    case UbifsNodeType.Xent: Result.Xents.Add(Entry.Node); break;
                }
            }
        }

        private static string DentNameAt(byte[] D, int Off)
        {
            if (Off + 0x38 > D.Length)
                return string.Empty;

            var NLen = BitConverter.ToUInt16(D, Off + 0x32);
            if ((NLen == 0) || (Off + 0x38 + NLen > D.Length))
                return string.Empty;

            return Encoding.UTF8.GetString(D, Off + 0x38, NLen);
        }

        private IEnumerable<uint> MappedLebs()
        {
            if (RawSource != null)
            {
                var Count = RawSource.Length / RawLebSize;
                for (long I = 0; I < Count; I++)
                    yield return (uint)I;
            }
            else
            {
                foreach (var LNum in Volume.LebToPeb.Keys)
                    yield return LNum;
            }
        }

        private LebNodeMap GetNodeMap(uint Lnum)
        {
            if (NodeMaps.TryGetValue(Lnum, out var Cached))
                return Cached;

            var Map = new LebNodeMap();
            NodeMaps[Lnum] = Map;

            var D = SafeLeb(Lnum);
            if (D == null)
                return Map;

            for (int Off = 0; Off + UbifsNode.HeaderSize <= D.Length; Off += 8)
            {
                if (!UbifsNode.IsValidNode(D, Off))
                    continue;

                Map.Add(D, Off, (UbifsNodeType)D[Off + 0x14], BitConverter.ToUInt64(D, Off + 0x08));
            }

            return Map;
        }

        private bool Collect(UbifsNode Node)
        {
            switch (Node.Type)
            {
                case UbifsNodeType.Ino: Inodes.Add(Node); return true;
                case UbifsNodeType.Dent: Dents.Add(Node); return true;
                case UbifsNodeType.Xent: Xents.Add(Node); return true;
                case UbifsNodeType.Data: Datas.Add(Node); return true;
                default: return false;
            }
        }

        private void BuildIndexes()
        {
            InodeByInum.Clear();
            DentsByParent.Clear();
            XentsByHost.Clear();
            DataByInum.Clear();

            foreach (var N in Inodes)
                InodeByInum[N.Key.Inum] = N;

            foreach (var N in Dents)
            {
                uint Parent = N.Key.Inum;
                if (!DentsByParent.TryGetValue(Parent, out var L))
                    DentsByParent[Parent] = L = new List<UbifsNode>();
                L.Add(N);
            }

            foreach (var N in Xents)
            {
                uint Host = N.Key.Inum;
                if (!XentsByHost.TryGetValue(Host, out var L))
                    XentsByHost[Host] = L = new List<UbifsNode>();
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

        public Dictionary<string, byte[]> GetRootXattrs() => GetXAttrs(RootInum);

        private Dictionary<string, byte[]> GetXAttrs(uint HostInum)
        {
            if (!XentsByHost.TryGetValue(HostInum, out var L))
                return null;

            var Res = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var X in L)
            {
                var Value = Inode((uint)DentInum(X));
                if (Value == null)
                    continue;

                Res[DentName(X)] = InoInlineData(Value);
            }

            return (Res.Count > 0) ? Res : null;
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
                    var Entry = new FilesystemEntry(ModeToType(Mode), Universal.Helper.FsHelper.CombinePath(Path, DentName(D)),
                        InoUid(Child), InoGid(Child), Mode & 0xFFF, Convert.ToUInt32(InoSize(Child)));

                    var Xattrs = GetXAttrs((uint)DentInum(D));
                    if (Xattrs != null)
                        Entry.Xattrs = Xattrs;

                    Res.Add(Entry);
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

