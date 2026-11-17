using NyaFs.Filesystem.Universal;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.SquashFs
{
    public class SquashFsReader : RawPacket, Universal.IFilesystemReader, Universal.IRootFsInfo, Universal.IRootXattrs
    {
        private Compression.BaseCompressor Comp;
        private Dictionary<long, byte[]> MetadataTable = new Dictionary<long, byte[]>();

        Types.SqFragmentBlockEntry[] FragmentEntries = null;
        uint[] IdTable = null;

        ulong XAttrTableStart = 0;
        uint XAttrIds = 0;
        long[] XAttrIdBlocks = null;
        Dictionary<uint, Dictionary<string, byte[]>> XAttrsCache = new Dictionary<uint, Dictionary<string, byte[]>>();

        bool DebugReader = false;

        public Types.SqCompressionType Compression => Superblock.CompressionId;

        public SquashFsReader(byte[] Data) : base(Data)
        {
            Init();
        }

        public SquashFsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename))
        {

        }

        protected virtual void Init()
        {
            if (Superblock.IsCorrect)
            {
                InitCompressor();
                if (Superblock.IdTableStart != 0xfffffffffffffffful) ReadIdTable();
                if (Superblock.ExportTableStart != 0xfffffffffffffffful) ReadExportTable();
                if (Superblock.FragmentTableStart != 0xfffffffffffffffful) ReadFragmentTable();
                if (Superblock.XAttrIdTableStart != 0xfffffffffffffffful) ReadXAttrIdTable();

                //var Root = GetRootDir();
                //var Entries = GetDirEntries(Root);

                //var Etc = ReadDir("lib/network/wwan");
                // var Init = Read("/init");
            }
        }

        private void ReadIdTable()
        {
            // Each metadata block of the id table holds at most 2048 u32 entries.
            var IdBlocksCount = (Superblock.IdCount + 2047) / 2048;
            var Entries = new List<uint>();
            int Remaining = Convert.ToInt32(Superblock.IdCount);

            for (int i = 0; i < IdBlocksCount; i++)
            {
                var Offset = Convert.ToInt64(Superblock.IdTableStart) + 0x08 * i;
                var Count = Math.Min(Remaining, 2048);
                var B = ReadMetadata((long)ReadUInt64(Offset), 0, Count * 4);

                for (int e = 0; e < Count; e++)
                    Entries.Add(B.ReadUInt32(e * 0x04));

                Remaining -= Count;
            }

            IdTable = Entries.ToArray();
        }

        private void ReadExportTable()
        {
        }

        private void ReadFragmentTable()
        {
            var FragmentBlocksCount = (Superblock.FragmentEntryCount + 511) / 512;
            var Entries = new List<Types.SqFragmentBlockEntry>();
            //int id = 0;

            for (int i = 0; i < FragmentBlocksCount; i++)
            {
                var Offset = Convert.ToInt64(Superblock.FragmentTableStart) + 0x08 * i;
                var Address = (long)ReadUInt64(Offset);
                var B = ReadMetadata(Address, 0, Superblock.FragmentEntryCount * 0x10);

                for (int e = 0; e < Superblock.FragmentEntryCount; e++)
                {
                    var Entry = new Types.SqFragmentBlockEntry(B, e * 0x10);

                    Entries.Add(Entry);

                    var FragData = ReadArray(Convert.ToInt64(Entry.Start), Entry.Size);
                    var UncompressedData = Entry.IsCompressed ? Comp.Decompress(FragData) : FragData;

                    //var FN = $"fragments/fragment_{FragData.Length:x06}_readed.bin";
                    //if(!System.IO.File.Exists(FN))
                    //    System.IO.File.WriteAllBytes(FN, FragData);

                    if (DebugReader) Debug.WriteLine($"ReadFragmentTable data sz {Entry.Size:x06}");
                }
            }

            FragmentEntries = Entries.ToArray();
        }

        private void ReadXAttrIdTable()
        {
            var TableStart = Convert.ToInt64(Superblock.XAttrIdTableStart);
            XAttrTableStart = ReadUInt64(TableStart);
            XAttrIds = ReadUInt32(TableStart + 0x08);
            // u32 unused at 0x0C

            // Each metadata block holds 8192 / 16 = 512 xattr ids.
            var BlocksCount = (Convert.ToInt64(XAttrIds) * 0x10 + 0x1FFF) / 0x2000;
            var Blocks = new long[BlocksCount];
            for (int i = 0; i < BlocksCount; i++)
                Blocks[i] = Convert.ToInt64(ReadUInt64(TableStart + 0x10 + i * 0x08));

            XAttrIdBlocks = Blocks;

            if (DebugReader) Debug.WriteLine($"Xattr id table: ids {XAttrIds} blocks {BlocksCount} table {XAttrTableStart:X08}");
        }

        private Types.SqXattrId ReadXAttrId(uint Index)
        {
            if ((XAttrIdBlocks == null) || (Index >= XAttrIds))
                return null;

            long Pos = Convert.ToInt64(Index) * 0x10;
            long BlockIndex = Pos / 0x2000;
            long Offset = Pos % 0x2000;

            if (BlockIndex >= XAttrIdBlocks.Length)
                return null;

            var Data = ReadMetadata(XAttrIdBlocks[BlockIndex], Offset, 0x10);
            return new Types.SqXattrId(Data, 0);
        }

        private Dictionary<string, byte[]> ReadXAttrs(uint Index)
        {
            if (XAttrIdBlocks == null)
                return null;

            if (XAttrsCache.TryGetValue(Index, out var Cached))
                return Cached;

            var Id = ReadXAttrId(Index);
            if ((Id == null) || (Id.Count == 0))
                return null;

            var Res = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var Cursor = new MetadataCursor(this, Convert.ToInt64(XAttrTableStart) + Id.XAttrBlock, Id.XAttrOffset);

            for (uint i = 0; i < Id.Count; i++)
            {
                var Header = Cursor.Read(0x04);
                int Type = Header.ReadUInt16(0);
                int NameSize = Header.ReadUInt16(2);
                var Name = Cursor.Read(NameSize);

                var ValueHeader = Cursor.Read(0x04);
                uint ValueSize = ValueHeader.ReadUInt32(0);
                byte[] Value;

                if ((Type & 0x100) != 0)
                {
                    // Out-of-line value: the entry contains a metadata reference to a value record.
                    var RefData = Cursor.Read(0x08);
                    ulong Ref = RefData.ReadUInt64(0);
                    var ValueCursor = new MetadataCursor(this,
                        Convert.ToInt64(XAttrTableStart) + Convert.ToInt64(Ref >> 16),
                        Convert.ToInt32(Ref & 0xFFFF));

                    var OolHeader = ValueCursor.Read(0x04);
                    uint OolSize = OolHeader.ReadUInt32(0);
                    Value = ValueCursor.Read(Convert.ToInt32(OolSize));
                }
                else
                    Value = Cursor.Read(Convert.ToInt32(ValueSize));

                var Prefix = GetXAttrPrefix(Type);
                if (Prefix == null)
                {
                    if (DebugReader) Debug.WriteLine($"SquashFs: unknown xattr type {Type & 0xFF}, skipped");
                    continue;
                }

                Res[Prefix + Encoding.UTF8.GetString(Name)] = Value;
            }

            XAttrsCache[Index] = Res;
            return Res;
        }

        private static string GetXAttrPrefix(int Type)
        {
            switch (Type & 0xFF)
            {
                case 0: return "user.";
                case 1: return "trusted.";
                case 2: return "security.";
                default: return null;
            }
        }

        private sealed class MetadataCursor
        {
            private readonly SquashFsReader Reader;
            private long Address;
            private long Offset;

            internal MetadataCursor(SquashFsReader Reader, long Address, long Offset)
            {
                this.Reader = Reader;
                this.Address = Address;
                this.Offset = Offset;
            }

            internal byte[] Read(int Size)
            {
                if (Size == 0)
                    return new byte[0];

                var Res = Reader.ReadMetadata(Address, Offset, Size);

                long Remaining = Size;
                while (Remaining > 0)
                {
                    var Block = Reader.GetMetadataBlock(Address);
                    long Available = Block.Length - Offset;

                    if (Remaining < Available)
                    {
                        Offset += Remaining;
                        Remaining = 0;
                    }
                    else
                    {
                        Remaining -= Available;
                        Address += 2 + (Reader.ReadUInt16(Address) & 0x7FFF);
                        Offset = 0;
                    }
                }

                return Res;
            }
        }

        private void InitCompressor()
        {
            switch (Superblock.CompressionId)
            {
                case Types.SqCompressionType.Lzma:
                    Comp = new Compression.Lzma();
                    break;
                case Types.SqCompressionType.Gzip:
                    Comp = (Superblock.Flags.HasFlag(Types.SqSuperblockFlags.COMPRESSOR_OPTIONS))
                        ? new Compression.Gzip(Raw, 0x60)
                        : new Compression.Gzip();
                    break;

                case Types.SqCompressionType.Xz:
                    Comp = (Superblock.Flags.HasFlag(Types.SqSuperblockFlags.COMPRESSOR_OPTIONS))
                        ? new Compression.Xz(Raw, 0x60)
                        : new Compression.Xz();
                    break;

                case Types.SqCompressionType.Lz4:
                    if (!Superblock.Flags.HasFlag(Types.SqSuperblockFlags.COMPRESSOR_OPTIONS))
                        throw new System.IO.IOException("LZ4 compression algorithm does not have a required Compression Options metatable.");
                    Comp = new Compression.Lz4(Raw, 0x60);
                    break;

                case Types.SqCompressionType.Lzo:
                    Comp = (Superblock.Flags.HasFlag(Types.SqSuperblockFlags.COMPRESSOR_OPTIONS))
                        ? new Compression.Lzo(Raw, 0x60)
                        : new Compression.Lzo();
                    break;

                case Types.SqCompressionType.Zstd:
                    Comp = (Superblock.Flags.HasFlag(Types.SqSuperblockFlags.COMPRESSOR_OPTIONS))
                        ? new Compression.Zstd(Raw, 0x60)
                        : new Compression.Zstd();
                    break;

                default:
                    throw new System.IO.IOException($"Unsupported compression algorithm {Superblock.CompressionId}");
            }
        }

        private Types.SqInode GetRootDir() => GetNode(Superblock.RootINodeRef);

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            var R = GetRootDir();
            if (R == null)
            {
                User = Group = Mode = 0;
                return false;
            }

            User = GetUID(R.UidIndex);
            Group = GetGID(R.GidIndex);
            Mode = R.Permissions & 0xFFF;
            return true;
        }

        public Dictionary<string, byte[]> GetRootXattrs() => GetXAttrs(GetRootDir());

        private Types.SqInode GetNode(Types.SqMetadataRef Ref)
        {
            var Metadata = ReadINodeMetadata(Ref, 0x10);
            var UnknownNode = new Types.SqInode(Metadata);

            switch (UnknownNode.InodeType)
            {
                case Types.SqInodeType.BasicDirectory:
                    {
                        Metadata = ReadINodeMetadata(Ref, 0x20);
                        var N = new Types.Nodes.BasicDirectory(Metadata);
                        Metadata = ReadINodeMetadata(Ref, N.INodeSize);
                        return new Types.Nodes.BasicDirectory(Metadata);
                    }
                case Types.SqInodeType.BasicSymlink:
                case Types.SqInodeType.ExtendedSymlink:
                    {
                        bool IsExtended = UnknownNode.InodeType == Types.SqInodeType.ExtendedSymlink;

                        Metadata = ReadINodeMetadata(Ref, 0x18);
                        var N = new Types.Nodes.BasicSymLink(Metadata, IsExtended);
                        Metadata = ReadINodeMetadata(Ref, N.INodeSize);
                        return new Types.Nodes.BasicSymLink(Metadata, IsExtended);
                    }

                case Types.SqInodeType.BasicFile:
                case Types.SqInodeType.ExtendedFile:
                    {
                        bool IsExtended = UnknownNode.InodeType == Types.SqInodeType.ExtendedFile;

                        // Extended headers are larger; reading them for basic files is harmless
                        // because the basic fields all live inside the first 0x20 bytes.
                        Metadata = ReadINodeMetadata(Ref, IsExtended ? 0x38 : 0x20);
                        var N = new Types.Nodes.BasicFile(Metadata, Superblock.BlockSize, IsExtended);
                        Metadata = ReadINodeMetadata(Ref, N.INodeSize);
                        return new Types.Nodes.BasicFile(Metadata, Superblock.BlockSize, IsExtended);
                    }

                case Types.SqInodeType.BasicBlockDevice:
                case Types.SqInodeType.BasicCharDevice:
                case Types.SqInodeType.ExtendedBlockDevice:
                case Types.SqInodeType.ExtendedCharDevice:
                    {
                        bool IsExtended = (UnknownNode.InodeType == Types.SqInodeType.ExtendedBlockDevice) ||
                                          (UnknownNode.InodeType == Types.SqInodeType.ExtendedCharDevice);

                        Metadata = ReadINodeMetadata(Ref, IsExtended ? 0x1C : 0x18);
                        return new Types.Nodes.BasicDevice(Metadata, IsExtended);
                    }

                case Types.SqInodeType.BasicFifo:
                case Types.SqInodeType.BasicSocket:
                case Types.SqInodeType.ExtendedFifo:
                case Types.SqInodeType.ExtendedSocket:
                    {
                        bool IsExtended = (UnknownNode.InodeType == Types.SqInodeType.ExtendedFifo) ||
                                          (UnknownNode.InodeType == Types.SqInodeType.ExtendedSocket);

                        Metadata = ReadINodeMetadata(Ref, IsExtended ? 0x18 : 0x14);
                        return new Types.Nodes.BasicIPC(Metadata, IsExtended);
                    }

                case Types.SqInodeType.ExtendedDirectory:
                    {
                        Metadata = ReadINodeMetadata(Ref, 0x28);
                        //var N = new Types.Nodes.ExtendedDirectory(Metadata);
                        //Metadata = ReadINodeMetadata(Ref, N.INodeSize);
                        return new Types.Nodes.ExtendedDirectory(Metadata);
                    }
                default: return UnknownNode;
            }
        }

        private Dictionary<string, byte[]> GetXAttrs(Types.SqInode Node)
        {
            if (Node == null)
                return null;

            uint Index = Node.XAttrIndex;
            if (Index == Types.SqInode.InvalidXAttr)
                return null;

            return ReadXAttrs(Index);
        }

        private byte[] ReadINodeMetadata(Types.SqMetadataRef Ref, long Size)
        {
            return ReadMetadata(Convert.ToInt64(Superblock.INodeTableStart) + Ref.Block, Ref.Offset, Size);
        }

        /// <summary>
        /// Readout data from metadata blocks
        /// </summary>
        /// <param name="Address">Address of metadata block</param>
        /// <param name="Offset">Offset to data in unpacked block</param>
        /// <param name="Size">Size of data to readout</param>
        /// <returns></returns>
        private byte[] GetMetadataBlock(long Address)
        {
            if (MetadataTable.TryGetValue(Address, out var Cached))
                return Cached;

            uint Header = ReadUInt16(Address);
            long DataSize = Header & 0x7FFFu;
            bool IsCompressed = (Header & 0x8000) == 0;
            byte[] Raw = ReadArray(Address + 2, DataSize);
            var Uncompressed = IsCompressed ? Comp.Decompress(Raw) : Raw;

            MetadataTable[Address] = Uncompressed;
            return Uncompressed;
        }

        private byte[] ReadMetadata(long Address, long Offset, long Size)
        {
            var Res = new byte[Size];
            long ResOffset = 0;

            while (true)
            {
                uint Header = ReadUInt16(Address);
                long DataSize = Header & 0x7FFFu;
                var Uncompressed = GetMetadataBlock(Address);

                if ((Uncompressed.Length - Offset) < Size)
                {
                    long Copy = Uncompressed.Length - Offset;
                    Res.WriteArray(ResOffset, Uncompressed.ReadArray(Offset, Copy), Copy);

                    Size -= Copy;
                    ResOffset += Copy;
                    Offset = 0;
                }
                else if ((Uncompressed.Length - Offset) == Size)
                {
                    Res.WriteArray(ResOffset, Uncompressed.ReadArray(Offset, Size), Size);
                    break;
                }
                else
                {
                    Res.WriteArray(ResOffset, Uncompressed.ReadArray(Offset, Size), Size);
                    break;
                }

                Address += DataSize + 2;
            }

            return Res;
        }

        internal Types.SqDirectoryEntry[] GetDirEntries(Types.Nodes.ExtendedDirectory Dir, string Name, uint INode)
        {
            var Raw = ReadMetadata(Convert.ToInt64(Superblock.DirectoryTableStart) + Dir.DirBlockStart, Dir.BlockOffset, Dir.FileSize);
            long Offset = 0;
            var DirEntries = new List<Types.SqDirectoryEntry>();

            while (Offset < Convert.ToInt64(Dir.FileSize) - 3)
            {
                var DirHeader = new Types.SqDirectoryHeader(Raw, Offset);
                Offset += DirHeader.getLength();

                for (int i = 0; i < DirHeader.Count + 1; i++)
                {
                    //if (DebugReader) Debug.WriteLine($"INode element: {Superblock.DirectoryTableStart + Dir.DirBlockStart + Dir.BlockOffset:X08}");
                    var E = new Types.SqDirectoryEntry(DirHeader.INodeNumber, DirHeader.Start, Raw, Offset);
                    DirEntries.Add(E);

                    Offset += E.getLength();
                }
            }

            return DirEntries.ToArray();
        }

        internal Types.SqDirectoryEntry[] GetDirEntries(Types.SqInode Dir, string Name, uint INode)
        {
            if (Dir is Types.Nodes.ExtendedDirectory Extended)
                return GetDirEntries(Extended, Name, INode);

            if (Dir is Types.Nodes.BasicDirectory Basic)
                return GetDirEntries(Basic, Name, INode);

            return null;
        }

        internal Types.SqDirectoryEntry[] GetDirEntries(Types.Nodes.BasicDirectory Dir, string Name, uint INode)
        {
            //if(DebugReader) Debug.WriteLine($"INode element read from DIR: {Superblock.DirectoryTableStart + Dir.DirBlockStart:X08}, offset {Dir.BlockOffset:X08} {Name} {INode}");

            var Raw = ReadMetadata(Convert.ToInt64(Superblock.DirectoryTableStart) + Dir.DirBlockStart, Dir.BlockOffset, Dir.FileSize);
            long Offset = 0;
            var DirEntries = new List<Types.SqDirectoryEntry>();
            while (Offset < Convert.ToInt64(Dir.FileSize) - 6)
            {
                var DirHeader = new Types.SqDirectoryHeader(Raw, Offset);
                //if (DebugReader) Debug.WriteLine($"INode header read from DIR {INode}: start {DirHeader.Start:X08} node: {DirHeader.INodeNumber:X08} count:{DirHeader.Count:X08} at address {Offset:X08}");
                Offset += DirHeader.getLength();

                for (int i = 0; i < DirHeader.Count + 1; i++)
                {
                    var E = new Types.SqDirectoryEntry(DirHeader.INodeNumber, DirHeader.Start, Raw, Offset);
                    //if (DebugReader) Debug.WriteLine($"INode element read from DIR {INode}: {E.Name} {Superblock.DirectoryTableStart + Dir.DirBlockStart:X08},{E.Reference.Block:X08},{E.Reference.Offset:X08}: offset {Offset:X08} ");
                    
                    DirEntries.Add(E);

                    Offset += E.getLength();
                }
            }


            return DirEntries.ToArray();
        }

        internal Types.SqInode GetINodeByPath(string Path)
        {
            if (Path.Length == 0)
                throw new ArgumentException($"{Path} is empty");

            var Root = GetRootDir();
            if (Root == null)
                return null;

            if ((Path == ".") || (Path == "/")) return Root;

            if (Path[0] == '/') Path = Path.Substring(1);
            var Parts = Path.Split('/');

            var Entries = GetDirEntries(Root, "/", 1);
            if (Entries == null)
                return null;
            for (int i = 0; i < Parts.Length; i++)
            {
                var P = Parts[i];

                bool Found = false;
                foreach (var I in Entries)
                {
                    if (I.Name == P)
                    {
                        if (DebugReader) Debug.WriteLine($"Read INode: {Superblock.INodeTableStart:x08} block {I.Reference.Block:x04} offset {I.Reference.Offset:x04} index {I.Inode}");

                        var N = GetNode(I.Reference);
                        if (i == Parts.Length - 1)
                            return N;

                        if ((N.InodeType == Types.SqInodeType.BasicDirectory) ||
                            (N.InodeType == Types.SqInodeType.ExtendedDirectory))
                        {
                            Entries = GetDirEntries(N, I.Name, I.Inode);
                            if (Entries == null)
                                return null;

                            Found = true;
                            break;
                        }
                        else
                            return null;
                    }
                }
                if (!Found)
                    return null;
            }

            return null;
        }

        private byte[] GetINodeContent(Types.Nodes.BasicFile N, string Path)
        {
            // The offset from the start of the archive where the data blocks are stored
            var BlockOffset = N.FragmentBlockOffset;
            var BlockSizes = N.BlockSizes;

            if (N.FileSize > Int32.MaxValue)
                throw new System.IO.IOException($"SquashFs: file {Path} is too large ({N.FileSize} bytes)");

            var Res = new byte[Convert.ToInt32(N.FileSize)];
            long Offset = 0;
            long SrcOffset = N.BlocksStart;

            if (DebugReader)
            {
                if (N.FragmentBlockIndex != 0xffffffff)
                    Debug.WriteLine($"Node {Path}: {N.FileSize}   blocks {BlockSizes.Length} fragment {N.FragmentBlockIndex} address {FragmentEntries[N.FragmentBlockIndex].Start:X08} off {N.FragmentBlockOffset:X08} size {N.FragmentSize:X08}");
                else
                    Debug.WriteLine($"Node {Path}: {N.FileSize}   blocks {BlockSizes.Length}");
            }

            for (int i = 0; i < BlockSizes.Length; i++)
            {
                // Bit 24 of the block size indicates a block stored uncompressed.
                bool IsUncompressed = (BlockSizes[i] & 0x1000000u) != 0;
                uint BlockLen = BlockSizes[i] & 0xFFFFFFu;

                var FragData = ReadArray(Convert.ToInt64(SrcOffset), BlockLen);
                if(DebugReader) Debug.WriteLine($"  Node block index {i} foffset {Offset} offset {SrcOffset} size {BlockLen} uncompressed {IsUncompressed}");
                var UncompressedData = IsUncompressed ? FragData : Comp.Decompress(FragData);
                Res.WriteArray(Offset, UncompressedData, UncompressedData.Length);
                Offset += UncompressedData.Length;

                SrcOffset += BlockLen;
            }

            if (N.FragmentBlockIndex != 0xffffffff)
            {
                var Frag = FragmentEntries[N.FragmentBlockIndex];
                var FragData = ReadArray(Convert.ToInt64(Frag.Start), Frag.Size);
                var UncompressedData = Frag.IsCompressed ? Comp.Decompress(FragData) : FragData;

                var OwnData = UncompressedData.ReadArray(N.FragmentBlockOffset, N.FragmentSize);
                Res.WriteArray(Offset, OwnData, OwnData.Length);

                //Debug.WriteLine($"  Node fragment {Path} foffset {Offset} fragoffset {N.FragmentBlockOffset} size {N.FragmentSize}");
                //System.IO.File.WriteAllBytes($"fragments/file/fragment_{N.FragmentSize:x06}_{Path.Replace('/', '_')}_readed_{Path}.bin", OwnData);
                
                Offset += N.FragmentSize;
            }

            if(Offset != Convert.ToInt64(N.FileSize))
                Debug.WriteLine($"  Node invalid readed size: readed {Offset} size {N.FileSize}");

            //if (Path == "usr/bin/AvaloniaSyncer/Avalonia.Controls.dll")
            //{
            //    System.IO.File.WriteAllBytes("Avalonia.Controls.dll", Res);
            //}
            return Res;
        }

        private uint GetGID(uint GidId)
        {
            return GetId(GidId);
        }

        private uint GetUID(uint UidId)
        {
            return GetId(UidId);
        }

        private uint GetId(uint Id)
        {
            if ((IdTable == null) || (Id >= IdTable.Length))
            {
                Log.Warning(0, $"SquashFs: user/group id index {Id} is out of range (id count {IdTable?.Length ?? 0}); using 0.");
                return 0;
            }

            return IdTable[Id];
        }

        /// <summary>
        /// Read file by path
        /// </summary>
        /// <param name="Path">Path to file</param>
        /// <returns>Content of file or null if file is not exists</returns>
        public byte[] Read(string Path)
        {
            var Node = GetINodeByPath(Path);

            if (Node != null)
            {
                switch (Node.InodeType)
                {
                    case Types.SqInodeType.BasicFile:
                    case Types.SqInodeType.ExtendedFile:
                        return GetINodeContent(Node as Types.Nodes.BasicFile, Path);
                    default:
                        return null;
                }
            }
            else
                return null;
        }

        /// <summary>
        /// Read device information
        /// </summary>
        /// <param name="Path">Path to device</param>
        /// <returns>Device numbers (major/minor)</returns>
        public Universal.Types.DeviceInfo ReadDevice(string Path)
        {
            var Node = GetINodeByPath(Path);

            if (Node != null)
            {
                switch (Node.InodeType)
                {
                    case Types.SqInodeType.BasicBlockDevice:
                    case Types.SqInodeType.BasicCharDevice:
                    case Types.SqInodeType.ExtendedBlockDevice:
                    case Types.SqInodeType.ExtendedCharDevice:
                        {
                            var Block = Node as Types.Nodes.BasicDevice;
                            return new Universal.Types.DeviceInfo(Block.Major, Block.Minor);
                        }
                    default:
                        return null;
                }
            }
            else
                return null;
        }

        /// <summary>
        /// Read link content by path
        /// </summary>
        /// <param name="Path">Path to symlink</param>
        /// <returns>Link</returns>
        public string ReadLink(string Path)
        {
            var Node = GetINodeByPath(Path);

            if (Node != null)
            {
                switch (Node.InodeType)
                {
                    case Types.SqInodeType.BasicSymlink:
                    case Types.SqInodeType.ExtendedSymlink:
                        return (Node as Types.Nodes.BasicSymLink).Target;
                    default:
                        return null;
                }
            }
            else
                return null;
        }

        /// <summary>
        /// Read directory content
        /// </summary>
        /// <param name="Path">Path to directory</param>
        /// <returns>Array of entries</returns>
        public FilesystemEntry[] ReadDir(string Path)
        {
            var DirNode = GetINodeByPath(Path);

            if (DirNode != null)
            {
                var Res = new List<FilesystemEntry>();
                Types.SqDirectoryEntry[] Entries = null;

                if (DirNode.InodeType == Types.SqInodeType.BasicDirectory)
                    Entries = GetDirEntries(DirNode as Types.Nodes.BasicDirectory, Path, DirNode.INodeNumber);
                else if (DirNode.InodeType == Types.SqInodeType.ExtendedDirectory)
                    Entries = GetDirEntries(DirNode as Types.Nodes.ExtendedDirectory, Path, DirNode.INodeNumber);
                else
                    return null;

                foreach (var E in Entries)
                {
                    if (DebugReader) Debug.WriteLine($"ENTRY {E.Inode} {E.Reference.Block:X04} {E.Reference.Offset:X08}");

                    var N = GetNode(E.Reference);
                    var G = GetGID(N.GidIndex);
                    var U = GetUID(N.UidIndex);
                    FilesystemEntry Entry;

                    // TODO: uid, gid conversion
                    switch (N.InodeType)
                    {
                        case Types.SqInodeType.BasicFile:
                        case Types.SqInodeType.ExtendedFile:
                            Entry = new FilesystemEntry(E.FsNodeType, Universal.Helper.FsHelper.CombinePath(Path, E.Name), U, G, N.Permissions, Convert.ToUInt32((N as Types.Nodes.BasicFile).FileSize));
                            break;
                        case Types.SqInodeType.BasicSymlink:
                        case Types.SqInodeType.ExtendedSymlink:
                            Entry = new FilesystemEntry(E.FsNodeType, Universal.Helper.FsHelper.CombinePath(Path, E.Name), U, G, N.Permissions, (N as Types.Nodes.BasicSymLink).TargetSize);
                            break;
                        default:
                            Entry = new FilesystemEntry(E.FsNodeType, Universal.Helper.FsHelper.CombinePath(Path, E.Name), U, G, N.Permissions, 0);
                            break;
                    }

                    var Xattrs = GetXAttrs(N);
                    if ((Xattrs != null) && (Xattrs.Count > 0))
                        Entry.Xattrs = Xattrs;

                    Res.Add(Entry);
                }

                return Res.ToArray();
            }
            else
                return null;
        }

        private Types.SqSuperblock Superblock => new Types.SqSuperblock(Raw, 0);
    }
}
