using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;

namespace NyaFs.Filesystem.SquashFs.Builder.Nodes
{
    class Dir : Node
    {
        public MetadataRef EntriesRef;
        public List<DirectoryEntry> Entries = new List<DirectoryEntry>();

        public uint Parent = 0;
        private uint Size = 0;

        public Dir(string Path, uint User, uint Group, uint Mode) : base(Types.SqInodeType.BasicDirectory, Path, User, Group, Mode)
        {

        }

        public void AddEntry(DirectoryEntry Entry)
        {
            Entries.Add(Entry);
        }

        private uint GetEntryBlockOffset()
        {
            if (Entries.Count > 0)
                return Convert.ToUInt32(Entries.First().NodeRef.MetadataOffset);
            else
                return 0;
        }

        public uint GetSize() => Size;

        public byte[] GetEntries()
        {
            var Res = new List<byte>();

            var Temp = new List<byte[]>();

            uint LastIndex = Entries.First().Node.Index;
            ulong LastMetadata = Entries.First().NodeRef.MetadataOffset;
            foreach (var E in Entries.OrderBy(E => E.Filename, StringComparer.Ordinal))
            {
                var Diff = E.Node.Index - LastIndex;

                if ((LastMetadata != E.NodeRef.MetadataOffset) || (Temp.Count == 255) || (Diff < 0) || (Diff > 255))
                {
                    //Debug.WriteLine($"Directory HEADER: index:{Index} offset:{LastMetadata} count:{Temp.Count:X08} block offset {Res.Count:X08}");

                    var Header = new Types.SqDirectoryHeader(Convert.ToUInt32(Temp.Count), Convert.ToUInt32(LastMetadata), LastIndex);
                    Res.AddRange(Header.getPacket());

                    foreach (var T in Temp)
                        Res.AddRange(T);

                    Temp.Clear();

                    LastIndex = E.Node.Index;
                    LastMetadata = E.NodeRef.MetadataOffset;
                }

                var DE = new Types.SqDirectoryEntry(LastIndex,
                                                    Convert.ToInt64(E.NodeRef.MetadataOffset),
                                                    E.Type,
                                                    Convert.ToUInt32(E.NodeRef.UnpackedOffset),
                                                    Diff,
                                                    E.Filename);

                //Debug.WriteLine($"Directory entry: {Index} {E.Filename} ref: {E.NodeRef.MetadataOffset:X08} {E.NodeRef.UnpackedOffset:X08} block offset {Res.Count:X08}");
                Temp.Add(DE.getPacket());
            }

            if (Temp.Count > 0)
            {
                //Debug.WriteLine($"Directory HEADER: index:{Index} offset:{LastMetadata} count:{Temp.Count:X08} block offset {Res.Count:X08}");

                var Header = new Types.SqDirectoryHeader(Convert.ToUInt32(Temp.Count), Convert.ToUInt32(LastMetadata), LastIndex);
                Res.AddRange(Header.getPacket());

                foreach (var T in Temp)
                    Res.AddRange(T);
            }
            //Debug.WriteLine($"Directory size: {Res.Count:X08}");

            Size = Convert.ToUInt32(Res.Count);

            return Res.ToArray();
        }

        public override Types.SqInode GetINode() => new Types.Nodes.BasicDirectory(Mode, UId, GId,
            Convert.ToUInt32(EntriesRef?.MetadataOffset ?? 0),
            Convert.ToUInt32(EntriesRef?.UnpackedOffset ?? 0),
            Convert.ToUInt32(Entries.Count + 2),
            Convert.ToUInt32(0),
            Parent);
    }
}
