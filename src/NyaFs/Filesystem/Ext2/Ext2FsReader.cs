using NyaFs.Filesystem.Universal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.Ext2
{
    public class Ext2FsReader : Ext2FsBase, Universal.IFilesystemReader, Universal.IRootFsInfo, Universal.IRootXattrs
    {
        public Ext2FsReader(byte[] Data) : base(Data) { }

        public Ext2FsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }

        private uint GetNodeSize(Types.ExtINode N)
        {
            switch(N.NodeType)
            {
                case Types.ExtINodeType.REG:
                case Types.ExtINodeType.LINK:
                    return N.SizeLo;
                default:
                    return 0;
            }
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
                if (Node.NodeType == Types.ExtINodeType.REG)
                    return GetINodeContent(Node);
                else
                    return null;
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
                if ((Node.NodeType == Types.ExtINodeType.CHAR) || (Node.NodeType == Types.ExtINodeType.BLOCK))
                {
                    var Raw = Node.Block[0];
                    var Major = Raw & 0xff;
                    var Minor = (Raw >> 8) & 0xFf;
                    return new Universal.Types.DeviceInfo(Major, Minor); // TODO
                }
                else
                    return null;
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
                if (Node.NodeType == Types.ExtINodeType.LINK)
                    return UTF8Encoding.UTF8.GetString(GetINodeContent(Node));
                else
                    return null;
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
                var Entries = GetDirEntries(DirNode);

                foreach(var E in Entries)
                {
                    if ((E.Name == ".") || (E.Name == "..")) continue;

                    var N = GetINode(E.INode);

                    var Entry = new Universal.FilesystemEntry(N.FsNodeType, Universal.Helper.FsHelper.CombinePath(Path, E.Name), N.UID, N.GID, N.Mode, GetNodeSize(N));
                    var X = ParseXattrs(N);
                    if (X != null)
                        Entry.Xattrs = X;

                    Res.Add(Entry);
                }

                return Res.ToArray();
            }
            else
                return null;
        }

        private static string XattrPrefix(byte Index)
        {
            switch (Index)
            {
                case 1: return "user.";
                case 2: return "system.posix_acl_access";
                case 3: return "system.posix_acl_default";
                case 4: return "trusted.";
                case 6: return "security.";
                case 7: return "system.";
                default: return null;
            }
        }

        public Dictionary<string, byte[]> GetRootXattrs() => ParseXattrs(GetRootDir());

        /// <summary>
        /// ext4 EA-inode value (e_value_inum): the value is stored as file data of a
        /// separate inode, e_value_size is the value length.
        /// </summary>
        private byte[] ReadXattrInode(uint Inum, uint Size)
        {
            if (Size > Int32.MaxValue)
                return null;

            var N = GetINode(Inum);
            if (N == null)
                return null;

            var Data = GetINodeContent(N);
            if (Data == null)
                return null;

            int Len = Convert.ToInt32(Size);
            if (Data.Length == Len)
                return Data;

            var Res = new byte[Len];
            Array.Copy(Data, Res, Math.Min(Data.Length, Len));
            return Res;
        }

        private Dictionary<string, byte[]> ParseXattrs(Types.ExtINode N)
        {
            Dictionary<string, byte[]> Res = null;
            ParseInlineXattrs(N, ref Res);
            ParseBlockXattrs(N, ref Res);
            return Res;
        }

        /// <summary>
        /// Inline (ibody) xattrs: header at 0x80 + i_extra_isize, entries after
        /// the 4-byte magic, values addressed relative to the first entry.
        /// </summary>
        private void ParseInlineXattrs(Types.ExtINode N, ref Dictionary<string, byte[]> Res)
        {
            if (INodeSize <= 0xA2)
                return;

            int ExtraIsize = Convert.ToInt32(N.GetUInt16(0x80));
            int HeaderOff = 0x80 + ExtraIsize;
            int End = Convert.ToInt32(INodeSize);

            if ((ExtraIsize < 4) || (HeaderOff + 4 > End))
                return;

            if (N.GetUInt32(HeaderOff) != 0xEA020000)
                return;

            int First = HeaderOff + 4;
            int Off = First;

            while (Off + 16 <= End)
            {
                if (N.GetUInt32(Off) == 0)
                    break;

                int NameLen = N.GetByte(Off);
                int Index = N.GetByte(Off + 1);
                int ValueOff = Convert.ToInt32(N.GetUInt16(Off + 2));
                uint ValueInum = N.GetUInt32(Off + 4);
                uint ValueSize = N.GetUInt32(Off + 8);

                var Prefix = XattrPrefix((byte)Index);
                if ((Prefix != null) && (NameLen >= 0) && (Off + 16 + NameLen <= End))
                {
                    byte[] Value = null;
                    if (ValueInum != 0)
                        Value = ReadXattrInode(ValueInum, ValueSize);
                    else if (ValueSize <= (uint)End)
                    {
                        int ValuePos = First + ValueOff;
                        if ((ValuePos >= First) && (ValuePos + Convert.ToInt32(ValueSize) <= End))
                            Value = N.GetBytes(ValuePos, Convert.ToInt32(ValueSize));
                    }

                    if (Value != null)
                    {
                        var Name = N.GetBytes(Off + 16, NameLen);
                        (Res ?? (Res = new Dictionary<string, byte[]>()))[Prefix + UTF8Encoding.UTF8.GetString(Name)] = Value;
                    }
                }

                if (NameLen == 0)
                    break;

                Off += 16 + ((NameLen + 3) & ~3);
            }
        }

        private void ParseBlockXattrs(Types.ExtINode N, ref Dictionary<string, byte[]> Res)
        {
            if (N.FileAcl == 0)
                return;

            long Base = (long)N.FileAcl * Superblock.BlockSize;
            if (ReadUInt32(Base) != 0xEA020000)
                return;

            int Off = 32;

            while (Off + 16 <= Superblock.BlockSize)
            {
                if (ReadUInt32(Base + Off) == 0)
                    break;

                int NameLen = ReadByte(Base + Off);
                int Index = ReadByte(Base + Off + 1);
                int ValueOff = ReadUInt16(Base + Off + 2);
                uint ValueInum = ReadUInt32(Base + Off + 4);
                uint ValueSize = ReadUInt32(Base + Off + 8);

                var Prefix = XattrPrefix((byte)Index);
                if ((Prefix != null) && (NameLen >= 0))
                {
                    byte[] Value = null;
                    if (ValueInum != 0)
                        Value = ReadXattrInode(ValueInum, ValueSize);
                    else if ((ValueSize < Superblock.BlockSize) && (ValueOff + ValueSize <= Superblock.BlockSize))
                        Value = ReadArray(Base + ValueOff, (int)ValueSize);

                    if (Value != null)
                    {
                        var Name = ReadArray(Base + Off + 16, NameLen);
                        (Res ?? (Res = new Dictionary<string, byte[]>()))[Prefix + UTF8Encoding.UTF8.GetString(Name)] = Value;
                    }
                }

                if (NameLen == 0)
                    break;

                Off += 16 + (((NameLen + 3) & ~3));
            }
        }

        internal Types.ExtINode GetINodeByPath(string Path)
        {
            if ((Path == ".") || (Path == "/")) return GetRootDir();
            if (Path.Length == 0)
                throw new ArgumentException($"{Path} is empty");

            if (Path[0] == '/') Path = Path.Substring(1);

            var Parts = Path.Split('/');

            var Entries = GetDirEntries(GetRootDir());

            for (int i = 0; i < Parts.Length; i++)
            {
                var P = Parts[i];

                bool Found = false;
                foreach (var I in Entries)
                {
                    if (I.Name == P)
                    {
                        var N = GetINode(I.INode);
                        if (i == Parts.Length - 1)
                            return N;

                        if (N.NodeType == Types.ExtINodeType.DIR)
                        {
                            Entries = GetDirEntries(N);
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


        public void Dump()
        {
            var BG = GetBlockGroup(0);

            Console.WriteLine($"          Block size: 0x{Superblock.BlockSize:x04}");
            Console.WriteLine($"         INode count: {Superblock.INodesCount}");

            Console.WriteLine($"    INode table size: {Superblock.INodesCount * Superblock.BlockSize / 1024} kB");
            Console.WriteLine($"         Free inodes: {BG.FreeINodesCountLo}");
        }

        internal Types.ExtDirectoryEntry[] GetDirEntries(Types.ExtINode Dir)
        {
            if (Dir.NodeType != Types.ExtINodeType.DIR) throw new ArgumentException("Cannot read dir entries from non-dir INode");

            var DirContent = GetINodeContent(Dir);
            List<Types.ExtDirectoryEntry> Entries = new List<Types.ExtDirectoryEntry>();

            long Offset = 0;
            var Entry = new Types.ExtDirectoryEntry(DirContent, Offset);
            while(Entry.INode != 0)
            {
                Entries.Add(Entry);

                Offset += Entry.RecordLength;
                if (Offset == DirContent.Length)
                    break;

                Entry = new Types.ExtDirectoryEntry(DirContent, Offset);
            }

            return Entries.OrderBy(E => E.Name).ToArray();
        }

        internal Types.ExtINode GetRootDir()
        {
            for (uint i = 1; i < Superblock.INodesCount; i++)
            {
                var N = GetINode(i);

                if (N.NodeType == Types.ExtINodeType.DIR)
                    return N;
            }
            return null;
        }

        public bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode)
        {
            var N = GetRootDir();
            if (N == null)
            {
                User = Group = Mode = 0;
                return false;
            }

            User = N.UID;
            Group = N.GID;
            Mode = N.Mode & 0xFFF;
            return true;
        }
    }
}

