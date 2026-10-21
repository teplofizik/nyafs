using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.CramFs.Types
{
    class CrSuperblock : ArrayWrapper
    {
        private readonly bool BigEndian;

        public CrSuperblock(bool BigEndian = false) : base(0x40)
        {
            this.BigEndian = BigEndian;
            Magic = 0x28cd3d45;
            Signature = "Compressed ROMFS";
            Name = "cramfs";
        }

        public CrSuperblock(byte[] Data, long Offset, bool BigEndian = false) : base(Data, Offset, 0x40)
        {
            this.BigEndian = BigEndian;
        }

        private uint ReadU32(long Offset) => BigEndian ? ReadUInt32BE(Offset) : ReadUInt32(Offset);

        private void WriteU32(long Offset, uint Value)
        {
            if (BigEndian)
                WriteUInt32BE(Offset, Value);
            else
                WriteUInt32(Offset, Value);
        }

        /// <summary>
        /// CramFS Magic value: 0x28cd3d45
        /// </summary>
        public uint Magic
        {
            get { return ReadU32(0); }
            set { WriteU32(0, value); }
        }

        /// <summary>
        /// Is Magic correct
        /// </summary>
        public bool IsMagicCorrect => (Magic == 0x28cd3d45u) && (Signature == "Compressed ROMFS");

        /// <summary>
        /// CramFS size
        /// </summary>
        public uint Size
        {
            get { return ReadU32(0x04); }
            set { WriteU32(0x04, value); }
        }

        /// <summary>
        /// CramFS flags
        /// </summary>
        public uint Flags
        {
            get { return ReadU32(0x08); }
            set { WriteU32(0x08, value); }
        }

        /// <summary>
        /// 
        /// </summary>
        public uint Future
        {
            get { return ReadU32(0x0C); }
            set { WriteU32(0x0C, value); }
        }

        /// <summary>
        /// Signature: "Compressed ROMFS"
        /// </summary>
        public string Signature
        {
            get { return ReadString(0x10, 0x10); }
            set { WriteString(0x10, value, 0x10); }
        }

        /// <summary>
        /// fsid.crc
        /// </summary>
        public uint FsIDCrc
        {
            get { return ReadU32(0x20); }
            set { WriteU32(0x20, value); }
        }

        /// <summary>
        /// fsid.edition 
        /// </summary>
        public uint FsIDEdition
        {
            get { return ReadU32(0x24); }
            set { WriteU32(0x24, value); }
        }

        /// <summary>
        /// fsid.blocks 
        /// </summary>
        public uint FsIDBlocks
        {
            get { return ReadU32(0x28); }
            set { WriteU32(0x28, value); }
        }

        /// <summary>
        /// fsid.files 
        /// </summary>
        public uint FsIDFiles
        {
            get { return ReadU32(0x2C); }
            set { WriteU32(0x2C, value); }
        }

        /// <summary>
        /// Name
        /// </summary>
        public string Name
        {
            get { return ReadString(0x30, 0x10); }
            set { WriteString(0x30, value, 0x10); }
        }

    }
}
