using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.CramFs.Types
{
    class CrNode : ArrayWrapper
    {
        // private int SizeWidth = 24;
        private readonly bool BigEndian;
        private readonly bool WideInodes;

        private int HeaderSize => WideInodes ? 0x10 : 0x0C;

        public CrNode(uint Mode, uint Uid, uint Gid, uint Size, string Filename, bool BigEndian = false,
            bool WideInodes = false) :
            base((WideInodes ? 0x10 : 0x0C) + Filename.Length.GetAligned(4))
        {
            this.BigEndian = BigEndian;
            this.WideInodes = WideInodes;

            this.Mode = Mode;
            this.Size = Size;
            UId = Uid;
            GId = Gid;

            NameLen = Convert.ToUInt32(Filename.Length);
            WriteString(HeaderSize, Filename, Filename.Length);
        }

        public CrNode(byte[] Data, long Offset, bool BigEndian = false, bool WideInodes = false) :
            base(Data, Offset, WideInodes ? 0x10 : 0x0C)
        {
            this.BigEndian = BigEndian;
            this.WideInodes = WideInodes;
            Resize(NodeSize);
        }

        /// <summary>
        /// Node size
        /// </summary>
        public long NodeSize => HeaderSize + NameLen;

        private uint ReadWord(long Offset) => BigEndian ? ReadUInt32BE(Offset) : ReadUInt32(Offset);

        private void WriteWord(long Offset, uint Value)
        {
            if (BigEndian)
                WriteUInt32BE(Offset, Value);
            else
                WriteUInt32(Offset, Value);
        }

        private uint ReadHalf(long Offset) => BigEndian ? ReadUInt16BE(Offset) : ReadUInt16(Offset);

        private void WriteHalf(long Offset, uint Value)
        {
            if (BigEndian)
                WriteUInt16BE(Offset, Value);
            else
                WriteUInt16(Offset, Value);
        }

        /// <summary>
        /// Mode (0, u16)
        /// </summary>
        public uint Mode
        {
            get { return ReadHalf(0x00); }
            set { WriteHalf(0x00, value); }
        }

        /// <summary>
        /// UID (2, u16)
        /// </summary>
        public uint UId
        {
            get { return ReadHalf(0x02); }
            set { WriteHalf(0x02, value); }
        }

        /// <summary>
        /// Size (4, u24)
        /// </summary>
        public uint Size
        {
            get
            {
                var Word = ReadWord(0x04);
                return BigEndian ? ((Word >> 8) & 0xFFFFFFu) : (Word & 0xFFFFFFu);
            }
            set
            {
                var Word = ReadWord(0x04);
                Word = BigEndian
                    ? ((Word & 0xFFu) | ((value & 0xFFFFFFu) << 8))
                    : ((Word & 0xFF000000u) | (value & 0xFFFFFFu));
                WriteWord(0x04, Word);
            }
        }

        /// <summary>
        /// Gid (7, u8)
        /// </summary>
        public uint GId
        {
            get { return BigEndian ? (ReadWord(0x04) & 0xFFu) : ReadByte(0x07); }
            set
            {
                if (BigEndian)
                {
                    var Word = ReadWord(0x04);
                    WriteWord(0x04, (Word & 0xFFFFFF00u) | (value & 0xFFu));
                }
                else
                    WriteByte(0x07, Convert.ToByte(value & 0xFF));
            }
        }

        /// <summary>
        /// NameLength (padded to 4 bytes)
        /// </summary>
        public uint NameLen
        {
            get
            {
                var Word = ReadWord(0x08);
                if (WideInodes)
                    return (Word >> 6) & 0x3FFu;

                return (BigEndian ? ((Word >> 26) & 0x3Fu) : (Word & 0x3Fu)) * 4;
            }
            set
            {
                var Aligned = (value + 3) & ~3u;
                var Word = ReadWord(0x08);
                if (WideInodes)
                    Word = (Word & 0x3Fu) | (Aligned << 6);
                else
                {
                    var Units = Aligned / 4;
                    Word = BigEndian
                        ? ((Word & 0x3FFFFFFu) | (Units << 26))
                        : ((Word & 0xFFFFFFC0u) | Units);
                }
                WriteWord(0x08, Word);
            }
        }

        /// <summary>
        /// Offset (byte offset, stored in 4-byte units)
        /// </summary>
        public uint Offset
        {
            get
            {
                if (WideInodes)
                    return ReadWord(0x0C) * 4;

                var Word = ReadWord(0x08);
                return (BigEndian ? (Word & 0x3FFFFFFu) : ((Word >> 6) & 0x3FFFFFFu)) * 4;
            }
            set
            {
                if (WideInodes)
                {
                    WriteWord(0x0C, value / 4);
                    return;
                }

                var Units = ((value + 3) / 4) & 0x3FFFFFFu;
                var Word = ReadWord(0x08);
                Word = BigEndian
                    ? ((Word & 0xFC000000u) | Units)
                    : ((Word & 0x0000003Fu) | (Units << 6));
                WriteWord(0x08, Word);
            }
        }

        /// <summary>
        /// Filesystem node type
        /// </summary>
        public Universal.Types.FilesystemItemType FsNodeType => Universal.Helper.FsHelper.GetFsNodeType(Mode);

        /// <summary>
        /// Mode as string
        /// </summary>
        public string ModeStr => Universal.Helper.FsHelper.GetModeString(Mode);

        /// <summary>
        /// Filename
        /// </summary>
        public string Name => ReadString(HeaderSize, NameLen).Replace("\0", "");
    }
}
