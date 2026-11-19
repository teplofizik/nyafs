using NyaFs.Filesystem.Universal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.CramFs
{
    public class CramFsBuilder : IFilesystemBuilder, IFilesystemStreamBuilder
    {
        private readonly bool BigEndian;
        private readonly bool WideInodes;
        private int BlockSize = 4096;

        public CramFsBuilder(bool BigEndian = false, bool WideInodes = false)
        {
            this.BigEndian = BigEndian;
            this.WideInodes = WideInodes;
        }

        List<uint> InvalidGid = new List<uint>();
        private List<Builder.Node> Nodes = new List<Builder.Node>();
        private Builder.Nodes.Dir Root = null;
        private uint BlocksCount = 0;

        private Builder.Nodes.Dir GetParentDirectory(string Path)
        {
            var Parent = Universal.Helper.FsHelper.GetParentDirPath(Path);

            foreach (var D in Nodes)
            {
                if (D.Path == Parent)
                    return D as Builder.Nodes.Dir;
            }

            return null;
        }

        private void CheckId(uint Uid, uint Gid)
        {
            if(Gid > 255)
            {
                if (!InvalidGid.Contains(Gid)) InvalidGid.Add(Gid);
            }
        }

        private void AddNestedNode(string Path, Func<Builder.Node> NodeGetter)
        {
            var Parent = GetParentDirectory(Path);
            if ((Parent != null) || (Path == "/"))
            {
                var N = NodeGetter();
                N.Index = Convert.ToUInt32(Nodes.Count + 1);

                CheckId(N.User, N.Group);

                if (Parent != null)
                    Parent.AddNestedNode(N);
                else
                    Root = N as Builder.Nodes.Dir;

                Nodes.Add(N);
            }
            else
                throw new InvalidOperationException($"Cannot add entry with path {Path}: no parent dir.");
        }


        /// <summary>
        /// Create block device
        /// </summary>
        /// <param name="Path">Path to block device</param>
        /// <param name="Major">Major number</param>
        /// <param name="Minor">Minor number</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.Block(Path, User, Group, Mode, Major, Minor));

        /// <summary>
        /// Create char device
        /// </summary>
        /// <param name="Path">Path to char device</param>
        /// <param name="Major">Major number</param>
        /// <param name="Minor">Minor number</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.Char(Path, User, Group, Mode, Major, Minor));

        /// <summary>
        /// Create directory
        /// </summary>
        /// <param name="Path">Path to directory (parent dir must exists)</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void Directory(string Path, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.Dir(Path, User, Group, Mode));

        /// <summary>
        /// Create fifo
        /// </summary>
        /// <param name="Path">Path to fifo</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void Fifo(string Path, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.Fifo(Path, User, Group, Mode));

        /// <summary>
        /// Create file
        /// </summary>
        /// <param name="Path">Path to file</param>
        /// <param name="Content">File content</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.File(Path, User, Group, Mode, Content));

        /// <summary>
        /// Create socket
        /// </summary>
        /// <param name="Path">Path to socket</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void Socket(string Path, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.Socket(Path, User, Group, Mode));

        /// <summary>
        /// Create symlink
        /// </summary>
        /// <param name="Path">Path to symlink</param>
        /// <param name="Target">Target path</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.SymLink(Path, User, Group, Mode, Target));

        public byte[] GetFilesystemImage()
        {
            using (var Stream = new MemoryStream())
            {
                WriteFilesystemImage(Stream);
                return Stream.ToArray();
            }
        }

        public void WriteFilesystemImage(System.IO.Stream Output)
        {
            if (!Output.CanSeek || !Output.CanRead)
                throw new NotSupportedException("CramFS streaming requires a readable, seekable output stream");

            if (InvalidGid.Count > 0)
            {
                Log.Warning(0, $"Filesystem containg group id which cannot be saved in cramfs image: {String.Join(", ", Array.ConvertAll(InvalidGid.ToArray(), G => $"{G}"))}");
            }

            var Counter = new TrackingStream(Output);
            AppendSuperblock(Counter);
            AppendNodes(Counter);
            AppendData(Counter);

            UpdateINodes(Output);
            UpdateSuperblock(Output, Counter.Length);
        }

        private void AppendSuperblock(TrackingStream Output)
        {
            var Superblock = new Types.CrSuperblock(BigEndian);
            var Packet = Superblock.getPacket();
            Output.Write(Packet, 0, Packet.Length);
        }

        private void PreprocessNodes(List<Builder.Nodes.Dir> Dirs, Builder.Nodes.Dir Base)
        {
            foreach (var N in Base.NestedNodes)
            {
                if (N.Type == Universal.Types.FilesystemItemType.Directory)
                {
                    var D = N as Builder.Nodes.Dir;
                    Dirs.Add(D);
                    PreprocessNodes(Dirs, D);
                }
            }
        }

        private void AppendNodes(TrackingStream Output)
        {
            var Dirs = new List<Builder.Nodes.Dir>();

            WriteNode(Output, Root);
            WriteDirEntries(Output, Root);

            PreprocessNodes(Dirs, Root);

            foreach (var D in Dirs)
                WriteDirEntries(Output, D);
        }

        private void WriteNode(TrackingStream Output, Builder.Node Node)
        {
            Node.RelevantNodeOffset = Convert.ToUInt32(Output.Length);
            Node.RelevantNode = Node.GenerateNode(BigEndian, WideInodes);
            var Packet = Node.RelevantNode.getPacket();
            Output.Write(Packet, 0, Packet.Length);
        }

        private void WriteDirEntries(TrackingStream Output, Builder.Nodes.Dir Dir)
        {
            Dir.DataOffset = Convert.ToUInt32(Output.Length);
            foreach (var Node in Dir.NestedNodes)
                WriteNode(Output, Node);

            Dir.DataSize = Convert.ToUInt32(Output.Length) - Dir.DataOffset;
        }

        private void AppendData(TrackingStream Output)
        {
            foreach (var Node in Nodes)
            {
                var Data = Node.Content;
                if (Data != null)
                {
                    Node.DataOffset = Convert.ToUInt32(Output.Length);
                    var Blocks = ConvertToBlocks(Data, Node.DataOffset);
                    Node.DataSize = Convert.ToUInt32(Data.Length);
                    Output.Write(Blocks, 0, Blocks.Length);
                }
            }
        }

        private void UpdateINodes(System.IO.Stream Output)
        {
            foreach (var N in Nodes)
            {
                if (N.RelevantNode == null)
                    continue;

                N.RelevantNode.Offset = N.DataOffset;
                if (N.RelevantNode.FsNodeType == Universal.Types.FilesystemItemType.Directory)
                    N.RelevantNode.Size = N.DataSize;

                var Packet = N.RelevantNode.getPacket();
                Output.Seek(N.RelevantNodeOffset, SeekOrigin.Begin);
                Output.Write(Packet, 0, Packet.Length);
            }
        }

        private void UpdateSuperblock(System.IO.Stream Output, long Size)
        {
            var Superblock = new Types.CrSuperblock(BigEndian);
            Superblock.Flags = 3;
            Superblock.FsIDFiles = Convert.ToUInt32(Nodes.Count);
            Superblock.FsIDBlocks = BlocksCount;
            Superblock.Size = Convert.ToUInt32(Size);
            Superblock.FsIDCrc = 0;

            var Packet = Superblock.getPacket();
            Output.Seek(0, SeekOrigin.Begin);
            Output.Write(Packet, 0, Packet.Length);

            Superblock.FsIDCrc = Crc32.Compute(Output, Size);
            Packet = Superblock.getPacket();
            Output.Seek(0, SeekOrigin.Begin);
            Output.Write(Packet, 0, Packet.Length);
        }

        private sealed class TrackingStream : System.IO.Stream
        {
            private readonly System.IO.Stream Target;
            private long Count;

            public TrackingStream(System.IO.Stream Target) { this.Target = Target; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => Count;
            public override long Position { get => Count; set => throw new NotSupportedException(); }
            public override void Write(byte[] Buffer, int Offset, int Count) { Target.Write(Buffer, Offset, Count); this.Count += Count; }
            public override void Flush() => Target.Flush();
            public override int Read(byte[] Buffer, int Offset, int Count) => throw new NotSupportedException();
            public override long Seek(long Offset, SeekOrigin Origin) => throw new NotSupportedException();
            public override void SetLength(long Value) => throw new NotSupportedException();
        }
        private List<byte[]> GetBlocks(byte[] Data)
        {
            long Offset = 0;
            var Res = new List<byte[]>();
            while(Offset < Data.Length)
            {
                long ToCopy = Data.Length - Offset;
                long Size = ToCopy > BlockSize ? BlockSize : ToCopy;

                byte[] Unpacked = Data.ReadArray(Offset, Size);
                byte[] Packed = Compression.Gzip.Compress(Unpacked);
                Res.Add(Packed);

                Offset += Size;
            }
            return Res;
        }

        /// <summary>
        /// Build table of blocks end!..
        /// </summary>
        /// <param name="Blocks"></param>
        /// <param name="BlocksOffset"></param>
        /// <returns></returns>
        private byte[] GetDataHeader(List<byte[]> Blocks, uint BlocksOffset)
        {
            var Res = new byte[Blocks.Count * 4];
            for (int i = 0; i < Blocks.Count; i++)
            {
                BlocksOffset = Convert.ToUInt32(BlocksOffset + Blocks[i].Length);

                if (BigEndian)
                    Res.WriteUInt32BE(i * 4, BlocksOffset);
                else
                    Res.WriteUInt32(i * 4, BlocksOffset);
            }
            return Res;
        }

        private byte[] ConvertToBlocks(byte[] Data, uint BaseOffset)
        {
            // Build data blocks...
            var Blocks = GetBlocks(Data);

            // Build blocks table...
            var Header = GetDataHeader(Blocks, Convert.ToUInt32(BaseOffset + Blocks.Count * 4));

            // Join all together...
            var Res = new List<byte>();
            Res.AddRange(Header);
            for(int i = 0; i < Blocks.Count; i++)
                Res.AddRange(Blocks[i]);

            // Add padding
            while (Res.Count % 4 != 0) Res.Add(0);

            BlocksCount += Convert.ToUInt32(Blocks.Count);
            return Res.ToArray();
        }

        private void UpdateSuperblock(byte[] preparedImage)
        {
            var SB = new Types.CrSuperblock(preparedImage, 0, BigEndian);

            SB.Flags = 3;
            SB.FsIDFiles = Convert.ToUInt32(Nodes.Count);
            SB.FsIDBlocks = BlocksCount;
            SB.Size = Convert.ToUInt32(preparedImage.Length);

            // Calc image crc
            SB.FsIDCrc = Crc32.CalcCrc(preparedImage);
        }

        private void UpdateINodes(byte[] preparedImage)
        {
            foreach(var N in Nodes)
            {
                var Node = new Types.CrNode(preparedImage, N.RelevantNodeOffset, BigEndian, WideInodes);

                Node.Offset = N.DataOffset;
                if (Node.FsNodeType == Universal.Types.FilesystemItemType.Directory)
                {
                    Node.Size = N.DataSize;
                }
            }
        }
    }
}
