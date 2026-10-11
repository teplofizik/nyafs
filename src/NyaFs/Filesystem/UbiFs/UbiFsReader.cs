using NyaFs.Filesystem.Universal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text;
using System.Xml.Linq;
using NyaIO.Data;
using NyaFs.Filesystem.UbiFs.Types;
using NyaFs.Processor.Scripting.Commands;
using SharpCompress.Common;
using System.Collections;
using System.Reflection.Metadata;

namespace NyaFs.Filesystem.UbiFs
{
    public class UbiFsReader : RawPacket, Universal.IFilesystemReader
    {
        bool DebugReader = true;
        int BlockSize = 0x40;
        int PhysicalBlockSize = 0x100;

        private List<PhysicalBlock> Blocks = new List<PhysicalBlock>();

        const uint UBI_EC_HDR_MAGIC = 0x55424923;
        const uint UBI_VID_HDR_MAGIC = 0x55424921;

        public UbiFsReader(byte[] Data) : base(Data)
        {
            Init();
        }

        public UbiFsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename))
        {

        }

        protected virtual void Init()
        {
            if (CheckMagic())
            {
                DetectBlocksSize();

                LoadBlocks();
            }
            else
                throw new InvalidOperationException("Invalid UBI image: no UBI_EC_HDR_MAGIC");
        }

        private bool CheckMagic()
        {
            return (ReadUInt32BE(0) == UBI_EC_HDR_MAGIC);
        }
        private void DetectBlocksSize()
        {
            DetectSmallBlockSize();
            DetectEraseBlockSize();
        }

        // UBI uses some amount of flash space for its own purposes, thus reducing the amount of flash space available for UBI users.Namely:

        //  2 PEBs are used to store the volume table;
        //  1 PEB is reserved for wear-leveling purposes;
        //  1 PEB is reserved for the atomic LEB change operation;
        //  some amount of PEBs are reserved for bad PEB handling; this is applicable for NAND flash but not for NOR flash; the amount of reserved PEBs is configurable and is equal to 20 blocks per 1024 blocks by default;
        //  UBI stores the EC and VID headers at the beginning of each PEB; the number of bytes used for these purposes depends on the flash type and is explained below.

        private void LoadBlocks()
        {
            int Offset = 0;
            while(Offset < getLength())
            {
                var B = new Types.PhysicalBlock(Raw, Offset, PhysicalBlockSize, BlockSize);

                if (B.Correct)
                {
                    if(DebugReader)
                        B.Dump();


                    Blocks.Add(B);
                }

                Offset += PhysicalBlockSize;
            }
        }

        private void DetectSmallBlockSize()
        {
            // TODO
            BlockSize = 0x800;

            // Scan: 0x80 0x100 0x200 0x400 0x800 0x1000 0x2000 for UBI! signature
            int Offset = 0x80;

            while((Offset < 0x10000) || (Offset < getLength()))
            {
                if(ReadUInt32BE(Offset) == UBI_VID_HDR_MAGIC)
                {
                    BlockSize = Offset;
                    return;
                }

                Offset <<= 1;
            }
            throw new InvalidOperationException("Invalid UBI image: cannot detect block size");
        }

        private void DetectEraseBlockSize()
        {
            // TODO
            PhysicalBlockSize = 0x1000;

            // Scan: 0x80 0x100 0x200 0x400 0x800 0x1000 0x2000 for UBI# signature
            int Offset = 0x80;

            while ((Offset < 0x1000000) || (Offset < getLength()))
            {
                if (ReadUInt32BE(Offset) == UBI_EC_HDR_MAGIC)
                {
                    PhysicalBlockSize = Offset;
                    return;
                }

                Offset <<= 1;
            }
            throw new InvalidOperationException("Invalid UBI image: cannot detect erase block size");
        }

        /// <summary>
        /// Read file by path
        /// </summary>
        /// <param name="Path">Path to file</param>
        /// <returns>Content of file or null if file is not exists</returns>
        public byte[] Read(string Path)
        {
            return null;
        }

        /// <summary>
        /// Read device information
        /// </summary>
        /// <param name="Path">Path to device</param>
        /// <returns>Device numbers (major/minor)</returns>
        public Universal.Types.DeviceInfo ReadDevice(string Path)
        {
            return null;
        }

        /// <summary>
        /// Read link content by path
        /// </summary>
        /// <param name="Path">Path to symlink</param>
        /// <returns>Link</returns>
        public string ReadLink(string Path)
        {
            return null;
        }

        /// <summary>
        /// Read directory content
        /// </summary>
        /// <param name="Path">Path to directory</param>
        /// <returns>Array of entries</returns>
        public FilesystemEntry[] ReadDir(string Path)
        {
            return null;
        }
    }
}
