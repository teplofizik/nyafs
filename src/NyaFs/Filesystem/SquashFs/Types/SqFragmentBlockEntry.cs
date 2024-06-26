﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.SquashFs.Types
{
    class SqFragmentBlockEntry : ArrayWrapper
    {
        public string Path = "";

        public SqFragmentBlockEntry(string Path, ulong Start, uint Size, bool Compressed) : base(0x10)
        { 
            this.Path = Path;
            this.Start = Start;
            this.Size = Size & 0xffffffu + (Compressed ? 0 : 0x01000000u);
        }

        public SqFragmentBlockEntry(byte[] Data, long Offset) : base(Data, Offset, 0x10)
        {

        }

        /// <summary>
        /// The offset within the archive where the fragment block starts
        /// u64 start (0x00) 
        /// </summary>
        public ulong Start
        {
            get { return ReadUInt64(0); }
            set { WriteUInt64(0, value); }
        }

        /// <summary>
        /// The offset within the archive where the fragment block starts
        /// u32 size (0x08) 
        /// </summary>
        public uint Size
        {
            get { return ReadUInt32(0x08) & 0xffffff; }
            set { WriteUInt32(0x08, (ReadUInt32(0x08) & 0x01000000) | (value & 0xffffff)); }
        }

        public bool IsCompressed
        {
            get { return (ReadUInt32(0x08) & 0x01000000) == 0; }
            set { 
                if(value)
                    WriteUInt32(0x08, ReadUInt32(0x08) | 0x01000000);
                else
                    WriteUInt32(0x08, ReadUInt32(0x08) & ~0x01000000u);
            }
        }
    }
}
