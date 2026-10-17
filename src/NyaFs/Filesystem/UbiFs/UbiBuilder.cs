using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.Filesystem.UbiFs.Types;

namespace NyaFs.Filesystem.UbiFs
{
    /// <summary>
    /// UBI container builder: writes erase-counter/volume-identifier headers, the layout
    /// volume (volume table) and the logical eraseblocks of each volume.
    /// </summary>
    public class UbiBuilder
    {
        public const uint UBI_VERSION = 1;
        public const uint UBI_EC_HDR_MAGIC = 0x55424923;   // "UBI#"
        public const uint UBI_VID_HDR_MAGIC = 0x55424921;  // "UBI!"
        public const uint UBI_LAYOUT_VOLUME_ID = 0x7FFFEFFF;
        public const uint UBI_LAYOUT_VOLUME_EBS = 2;

        public readonly int PebSize;
        public readonly int PageSize;      // min_io_size / vid_hdr_offset
        public readonly int DataOffset;    // 2 * PageSize
        public int LebSize => PebSize - DataOffset;

        public uint ImageSequence = 0x72B8C7D5;

        public class Volume
        {
            public uint Id;
            public string Name;
            public VolumeType Type = VolumeType.UBI_VID_DYNAMIC;
            public bool AutoResize;
            public List<byte[]> Lebs = new List<byte[]>();
        }

        private readonly List<Volume> Volumes = new List<Volume>();

        public UbiBuilder(int PebSize, int PageSize)
        {
            this.PebSize = PebSize;
            this.PageSize = PageSize;
            DataOffset = 2 * PageSize;
        }

        public Volume AddVolume(uint Id, string Name, VolumeType Type = VolumeType.UBI_VID_DYNAMIC, bool AutoResize = true)
        {
            var V = new Volume { Id = Id, Name = Name, Type = Type, AutoResize = AutoResize };
            Volumes.Add(V);
            return V;
        }

        public byte[] Build()
        {
            int TotalPebs = (int)UBI_LAYOUT_VOLUME_EBS;
            foreach (var V in Volumes)
                TotalPebs += V.Lebs.Count;

            var Image = new byte[(long)TotalPebs * PebSize];
            Fill(Image, 0xFF);

            // layout volume (2 PEBs) holds the volume table
            WritePeb(Image, 0, UBI_LAYOUT_VOLUME_ID, 0, (uint)(UBI_LAYOUT_VOLUME_EBS * LebSize));
            WritePeb(Image, 1, UBI_LAYOUT_VOLUME_ID, 1, (uint)(UBI_LAYOUT_VOLUME_EBS * LebSize));
            WriteVolumeTable(Image, 0);
            WriteVolumeTable(Image, 1);

            // volume LEBs
            int Peb = (int)UBI_LAYOUT_VOLUME_EBS;
            foreach (var V in Volumes)
            {
                for (int i = 0; i < V.Lebs.Count; i++)
                {
                    WritePeb(Image, Peb, V.Id, (uint)i, (uint)(V.Lebs.Count * LebSize));

                    var Leb = V.Lebs[i];
                    Array.Copy(Leb, 0, Image, (long)Peb * PebSize + DataOffset, Math.Min(Leb.Length, LebSize));
                    Peb++;
                }
            }

            return Image;
        }

        /// <summary>Write the EC header, the VID header and the (zero/0xFF) padding of one PEB.</summary>
        private void WritePeb(byte[] Image, int Peb, uint VolId, uint Lnum, uint DataSize)
        {
            long Base = (long)Peb * PebSize;

            // Erase counter header (64 bytes) at 0
            WriteBE32(Image, Base + 0x00, UBI_EC_HDR_MAGIC);
            Image[Base + 0x04] = (byte)UBI_VERSION;
            WriteBE64(Image, Base + 0x08, 0);                 // ec
            WriteBE32(Image, Base + 0x10, (uint)PageSize);    // vid_hdr_offset
            WriteBE32(Image, Base + 0x14, (uint)DataOffset);  // data_offset
            WriteBE32(Image, Base + 0x18, ImageSequence);
            WriteBE32(Image, Base + 0x3C, Crc32.CalcCrc(Slice(Image, Base, 0x3C)));

            // Volume identifier header (64 bytes) at vid_hdr_offset
            long Vid = Base + PageSize;
            WriteBE32(Image, Vid + 0x00, UBI_VID_HDR_MAGIC);
            Image[Vid + 0x04] = (byte)UBI_VERSION;
            Image[Vid + 0x05] = (byte)VolumeType.UBI_VID_DYNAMIC; // vol_type
            Image[Vid + 0x06] = 0;                                // copy_flag
            Image[Vid + 0x07] = 0;                                // compat
            WriteBE32(Image, Vid + 0x08, VolId);
            WriteBE32(Image, Vid + 0x0C, Lnum);
            WriteBE32(Image, Vid + 0x14, 0);                      // data_size (0 for dynamic volumes)
            WriteBE32(Image, Vid + 0x18, 0);                      // used_ebs = 0 for dynamic volumes
            WriteBE32(Image, Vid + 0x1C, 0);                      // data_pad
            WriteBE32(Image, Vid + 0x20, 0);                      // data_crc (0 for dynamic volumes)
            WriteBE64(Image, Vid + 0x28, 0);                      // sqnum
            WriteBE32(Image, Vid + 0x3C, Crc32.CalcCrc(Slice(Image, Vid, 0x3C)));
        }

        /// <summary>Write the volume table records into a layout-volume PEB's data area.</summary>
        private void WriteVolumeTable(byte[] Image, int Peb)
        {
            long Base = (long)Peb * PebSize + DataOffset;

            // zero the table area (records + a terminating zero record)
            for (long z = 0; z < (Volumes.Count + 1) * VolumeTableRecord.Size; z++)
                Image[Base + z] = 0;

            for (int i = 0; i < Volumes.Count; i++)
            {
                var V = Volumes[i];
                long Off = Base + i * VolumeTableRecord.Size;

                WriteBE32(Image, Off + 0x00, (uint)V.Lebs.Count);   // reserved_pebs
                WriteBE32(Image, Off + 0x04, 1);                    // alignment
                WriteBE32(Image, Off + 0x08, 0);                    // data_pad
                Image[Off + 0x0C] = (byte)V.Type;                   // vol_type
                Image[Off + 0x0D] = 0;                              // upd_marker
                WriteBE16(Image, Off + 0x0E, (ushort)V.Name.Length);
                var NameBytes = Encoding.ASCII.GetBytes(V.Name);
                Array.Copy(NameBytes, 0, Image, Off + 0x10, NameBytes.Length);
                Image[Off + 0x90] = (byte)(V.AutoResize ? 1 : 0);   // flags (UBI_VTBL_AUTORESIZE_FLG)
                WriteBE32(Image, Off + 0xA8, Crc32.CalcCrc(Slice(Image, Off, 0xA8)));
            }
        }

        private static byte[] Slice(byte[] Data, long Offset, int Len)
        {
            var Res = new byte[Len];
            Array.Copy(Data, Offset, Res, 0, Len);
            return Res;
        }

        private static void Fill(byte[] Data, byte Value)
        {
            for (int i = 0; i < Data.Length; i++)
                Data[i] = Value;
        }

        private static void WriteBE16(byte[] D, long O, ushort V)
        {
            D[O] = (byte)(V >> 8);
            D[O + 1] = (byte)V;
        }

        private static void WriteBE32(byte[] D, long O, uint V)
        {
            D[O] = (byte)(V >> 24);
            D[O + 1] = (byte)(V >> 16);
            D[O + 2] = (byte)(V >> 8);
            D[O + 3] = (byte)V;
        }

        private static void WriteBE64(byte[] D, long O, ulong V)
        {
            for (int i = 0; i < 8; i++)
                D[O + i] = (byte)(V >> (56 - i * 8));
        }
    }
}
