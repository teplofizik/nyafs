using NyaFs.Filesystem.Universal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.RomFs
{
    public class RomFsBuilder : IFilesystemBuilder, IFilesystemStreamBuilder
    {
        List<uint> InvalidGid = new List<uint>();
        List<uint> InvalidUid = new List<uint>();

        private List<Builder.Node> Nodes = new List<Builder.Node>();
        private Builder.Nodes.Dir Root = null;

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
            if (Uid != 0)
            {
                if (!InvalidUid.Contains(Uid)) InvalidUid.Add(Uid);
            }
            if (Gid != 0)
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
            AddNestedNode(Path, () => new Builder.Nodes.Block(Path, Mode, Major, Minor));

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
            AddNestedNode(Path, () => new Builder.Nodes.Char(Path, Mode, Major, Minor));

        /// <summary>
        /// Create directory
        /// </summary>
        /// <param name="Path">Path to directory (parent dir must exists)</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void Directory(string Path, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.Dir(Path, Mode));

        /// <summary>
        /// Create fifo
        /// </summary>
        /// <param name="Path">Path to fifo</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void Fifo(string Path, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.Fifo(Path, Mode));

        /// <summary>
        /// Create file
        /// </summary>
        /// <param name="Path">Path to file</param>
        /// <param name="Content">File content</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.File(Path, Mode, Content));

        /// <summary>
        /// Create socket
        /// </summary>
        /// <param name="Path">Path to socket</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void Socket(string Path, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.Socket(Path, Mode));

        /// <summary>
        /// Create symlink
        /// </summary>
        /// <param name="Path">Path to symlink</param>
        /// <param name="Target">Target path</param>
        /// <param name="User">Owner user</param>
        /// <param name="Group">Owner group</param>
        /// <param name="Mode">Access mode</param>
        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) =>
            AddNestedNode(Path, () => new Builder.Nodes.SymLink(Path, Mode, Target));

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
            if (!Output.CanSeek)
                throw new NotSupportedException("RomFS streaming requires a seekable output stream");

            if (InvalidGid.Count > 0)
            {
                Log.Warning(0, $"Filesystem containg group id which cannot be saved in romfs image: {String.Join(", ", Array.ConvertAll(InvalidGid.ToArray(), G => $"{G}"))}");
            }
            if (InvalidUid.Count > 0)
            {
                Log.Warning(0, $"Filesystem containg user id which cannot be saved in romfs image: {String.Join(", ", Array.ConvertAll(InvalidUid.ToArray(), U => $"{U}"))}");
            }

            var Counter = new TrackingStream(Output);
            var Superblock = new Types.RmSuperblock();
            Counter.Write(Superblock.getPacket(), 0, Superblock.getPacket().Length);

            Root.FileOffset = Counter.Length;
            AddDirectoryNodes(Counter, Root);

            UpdateNodes(Output);
            UpdateSuperblock(Output, Counter);
        }

        private void AddNodeWithContent(TrackingStream Output, List<Builder.Node> LocalNodes, Builder.Node Node)
        {
            Node.FileOffset = Output.Length;

            var Header = Node.GetHeader();
            Output.Write(Header.getPacket(), 0, Header.getPacket().Length);

            var Content = Node.GetContent();
            Output.Write(Content, 0, Content.Length);

            LocalNodes.Add(Node);
        }

        private void AddParentNode(TrackingStream Output, List<Builder.Node> LocalNodes, long Offset)
        {
            var Node = new Builder.Nodes.Parent(Offset);
            AddNodeWithContent(Output, LocalNodes, Node);
            Nodes.Add(Node);
        }

        private void AddDirectoryNodes(TrackingStream Output, Builder.Nodes.Dir Dir)
        {
            var LocalNodes = new List<Builder.Node>();

            var StartOffset = Output.Length;
            AddNodeWithContent(Output, LocalNodes, Dir);
            var NestedOffset = Output.Length;
            AddParentNode(Output, LocalNodes, StartOffset);

            foreach (var Node in Dir.NestedNodes)
            {
                if (Node.Type == Universal.Types.FilesystemItemType.Directory)
                {
                    Node.FileOffset = Output.Length;
                    LocalNodes.Add(Node);
                    AddDirectoryNodes(Output, Node as Builder.Nodes.Dir);
                }
                else
                    AddNodeWithContent(Output, LocalNodes, Node);
            }

            for (int I = LocalNodes.Count - 1; I > 0; I--)
                LocalNodes[I - 1].NextOffset = LocalNodes[I].FileOffset;

            Dir.DirLink = (Dir.Path == "/") ? StartOffset : NestedOffset;
        }

        private void UpdateNodes(System.IO.Stream Output)
        {
            foreach (var N in Nodes)
            {
                var Node = N.GetHeader();
                Node.NextHeader = Convert.ToUInt32(N.NextOffset);
                if (N.Type == Universal.Types.FilesystemItemType.Directory)
                    Node.SpecInfo = Convert.ToUInt32((N as Builder.Nodes.Dir).DirLink);

                Node.CalcChecksum();

                var Packet = Node.getPacket();
                Output.Seek(N.FileOffset, SeekOrigin.Begin);
                Output.Write(Packet, 0, Packet.Length);
            }
        }

        private void UpdateSuperblock(System.IO.Stream Output, TrackingStream Counter)
        {
            var Superblock = new Types.RmSuperblock();
            Superblock.FullSize = Convert.ToUInt32(Counter.Length);
            Superblock.Checksum = CalcChecksum(Counter.FirstBytes);

            var Packet = Superblock.getPacket();
            Output.Seek(0, SeekOrigin.Begin);
            Output.Write(Packet, 0, Packet.Length);
        }

        private sealed class TrackingStream : System.IO.Stream
        {
            private readonly System.IO.Stream Target;
            private long Count;
            public readonly byte[] FirstBytes = new byte[512];

            public TrackingStream(System.IO.Stream Target) { this.Target = Target; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => Count;
            public override long Position { get => Count; set => throw new NotSupportedException(); }
            public override void Write(byte[] Buffer, int Offset, int Count)
            {
                var Copy = (int)Math.Min(Count, Math.Max(0, FirstBytes.Length - this.Count));
                if (Copy > 0)
                    Array.Copy(Buffer, Offset, FirstBytes, (int)this.Count, Copy);

                Target.Write(Buffer, Offset, Count);
                this.Count += Count;
            }
            public override void Flush() => Target.Flush();
            public override int Read(byte[] Buffer, int Offset, int Count) => throw new NotSupportedException();
            public override long Seek(long Offset, SeekOrigin Origin) => throw new NotSupportedException();
            public override void SetLength(long Value) => throw new NotSupportedException();
        }
        private uint CalcChecksum(byte[] preparedImage)
        {
            int Count = Math.Min(512, preparedImage.Length) / 4;
            uint Res = 0;

            for(int i = 0; i < Count; i++)
                Res += preparedImage.ReadUInt32BE(i * 4);

            Res = Convert.ToUInt32((-Res) & 0xFFFFFFFF);
            return Res;
        }

        private void UpdateSuperblock(byte[] preparedImage)
        {
            var SB = new Types.RmSuperblock(preparedImage, 0);

            SB.FullSize = Convert.ToUInt32(preparedImage.Length);
            SB.Checksum = CalcChecksum(preparedImage);
        }

        private void UpdateNodes(byte[] preparedImage)
        {
            foreach(var N in Nodes)
            {
                var Node = new Types.RmNode(preparedImage, N.FileOffset);
                Node.NextHeader = Convert.ToUInt32(N.NextOffset);
                if(N.Type == Universal.Types.FilesystemItemType.Directory)
                    Node.SpecInfo = Convert.ToUInt32((N as Builder.Nodes.Dir).DirLink);

                Node.CalcChecksum();
            }
        }
    }
}