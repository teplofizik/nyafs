using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.Ext2
{
    /// <summary>
    /// ext3 filesystem builder: the ext2 layout plus a journal, generated from scratch.
    /// Sets the HAS_JOURNAL feature, points s_journal_inum at inode 8 and writes an empty
    /// (clean, s_start == 0) JBD2 journal superblock there.
    /// </summary>
    public class Ext3FsBuilder : Ext2FsBuilder
    {
        protected const uint JOURNAL_INODE = 8;
        protected const uint COMPAT_HAS_JOURNAL = 0x4;
        protected const uint JBD2_MAGIC = 0xC03B3998;
        protected const uint JBD2_SUPERBLOCK_V2 = 4;

        /// <summary>
        /// Journal size in blocks (1 MiB with the 1k blocks this builder uses).
        /// </summary>
        protected virtual uint JournalBlocks => 1024;

        public Ext3FsBuilder(uint DiskBlockSize) : base(DiskBlockSize)
        {
            InitExt3();
        }

        private void InitExt3()
        {
            Superblock.RevLevel = 1;
            Superblock.INodeSize = 128;
            Superblock.FirstNonreservedINode = 11;
            Superblock.FeatureCompatible |= COMPAT_HAS_JOURNAL;
            Superblock.JournalINode = JOURNAL_INODE;
            Superblock.JournalDevice = 0;

            BuildJournal();
        }

        protected void BuildJournal()
        {
            var Content = BuildJournalContent(JournalBlocks);

            var Node = GetINode(JOURNAL_INODE);
            Node.Mode = (uint)Types.ExtINodeType.REG | 0x600; // 0600
            Node.UID = 0;
            Node.GID = 0;
            Node.LinksCount = 1;
            Node.BlocksCount = 0;
            Node.BlocksLo = 0;
            Node.Block = new uint[15];

            MarkINodeAsUsed(JOURNAL_INODE);

            SetNodeBlockContent(Node, Content);
        }

        /// <summary>
        /// A JBD2 journal superblock (v2, big-endian) followed by zeroed journal blocks.
        /// s_start = 0 marks the journal clean, so the kernel performs no recovery.
        /// </summary>
        protected byte[] BuildJournalContent(uint Blocks)
        {
            var Content = new byte[Blocks * Superblock.BlockSize];
            var Uuid = Superblock.UUID;

            WriteBE32(Content, 0x00, JBD2_MAGIC);              // h_magic
            WriteBE32(Content, 0x04, JBD2_SUPERBLOCK_V2);      // h_blocktype
            WriteBE32(Content, 0x08, 1);                       // h_sequence
            WriteBE32(Content, 0x0C, Superblock.BlockSize);    // s_blocksize
            WriteBE32(Content, 0x10, Blocks);                  // s_maxlen
            WriteBE32(Content, 0x14, 1);                       // s_first
            WriteBE32(Content, 0x18, 1);                       // s_sequence
            WriteBE32(Content, 0x1C, 0);                       // s_start (clean)
            WriteBE32(Content, 0x20, 0);                       // s_errno
            WriteBE32(Content, 0x24, 0);                       // s_feature_compat
            WriteBE32(Content, 0x28, 0);                       // s_feature_incompat
            WriteBE32(Content, 0x2C, 0);                       // s_feature_ro_compat
            Array.Copy(Uuid, 0, Content, 0x30, 16);            // s_uuid
            WriteBE32(Content, 0x40, 1);                       // s_nr_users
            Array.Copy(Uuid, 0, Content, 0x58, 16);            // s_users[0]

            return Content;
        }

        protected static void WriteBE32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset + 0] = (byte)(Value >> 24);
            Data[Offset + 1] = (byte)(Value >> 16);
            Data[Offset + 2] = (byte)(Value >> 8);
            Data[Offset + 3] = (byte)Value;
        }
    }
}
