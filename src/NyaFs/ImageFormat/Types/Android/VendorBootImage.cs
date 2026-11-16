using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Types.Android
{
    /// <summary>
    /// Android vendor_boot image (AOSP vendor_boot_img_hdr_v3/v4), magic "VNDRBOOT".
    /// Layout: header page, vendor_ramdisk region, dtb, [v4: ramdisk table, bootconfig].
    /// </summary>
    public class VendorBootImage
    {
        public const int HeaderV3Size = 0x840;
        public const int HeaderV4Size = 0x850;
        public const int TableEntryV4Size = 108;
        public const int ArgsSize = 0x800;
        public const int NameSize = 0x10;

        public class Entry
        {
            public uint Size;
            public uint Offset;
            public uint Type;
            public string Name = "";
            public uint[] BoardId;

            public long FileOffset(long RegionStart) => RegionStart + Offset;
        }

        private readonly byte[] Raw;

        public bool Correct { get; private set; }
        public uint HeaderVersion { get; private set; }
        public uint PageSize { get; private set; }
        public uint KernelAddr { get; private set; }
        public uint RamdiskAddr { get; private set; }
        public uint VendorRamdiskSize { get; private set; }
        public uint TagsAddr { get; private set; }
        public string Cmdline { get; private set; } = "";
        public string Name { get; private set; } = "";
        public uint HeaderSize { get; private set; }
        public uint DtbSize { get; private set; }
        public ulong DtbAddr { get; private set; }
        public uint TableSize { get; private set; }
        public uint TableEntryNum { get; private set; }
        public uint TableEntrySize { get; private set; }
        public uint BootconfigSize { get; private set; }

        public List<Entry> Entries { get; } = new List<Entry>();

        public long VendorRamdiskOffset => PageSize;
        public long DtbOffset => Align(VendorRamdiskOffset + VendorRamdiskSize, PageSize);
        public long TableOffset => Align(DtbOffset + DtbSize, PageSize);
        public long BootconfigOffset => Align(TableOffset + TableSize, PageSize);

        public VendorBootImage(byte[] Raw)
        {
            this.Raw = Raw;
            if ((Raw == null) || (Raw.Length < HeaderV3Size))
                return;

            if ((Raw[0] != 'V') || (Raw[1] != 'N') || (Raw[2] != 'D') || (Raw[3] != 'R') ||
                (Raw[4] != 'B') || (Raw[5] != 'O') || (Raw[6] != 'O') || (Raw[7] != 'T'))
                return;

            HeaderVersion = ReadU32(0x08);
            if ((HeaderVersion != 3) && (HeaderVersion != 4))
                return;

            PageSize = ReadU32(0x0C);
            if ((PageSize < 0x200) || (PageSize > 0x10000) || ((PageSize & (PageSize - 1)) != 0))
                return;

            KernelAddr = ReadU32(0x10);
            RamdiskAddr = ReadU32(0x14);
            VendorRamdiskSize = ReadU32(0x18);
            Cmdline = ReadString(0x1C, ArgsSize);
            TagsAddr = ReadU32(0x81C);
            Name = ReadString(0x820, NameSize);
            HeaderSize = ReadU32(0x830);
            DtbSize = ReadU32(0x834);
            DtbAddr = ReadU64(0x838);

            if (HeaderVersion >= 4)
            {
                TableSize = ReadU32(0x840);
                TableEntryNum = ReadU32(0x844);
                TableEntrySize = ReadU32(0x848);
                BootconfigSize = ReadU32(0x84C);

                if ((TableEntrySize != 0) && (TableEntrySize < TableEntryV4Size))
                    return;

                ParseTable();
            }

            Correct = (HeaderSize >= ((HeaderVersion >= 4) ? HeaderV4Size : HeaderV3Size));
        }

        private void ParseTable()
        {
            var Table = ReadArray(TableOffset, TableSize);
            if (Table.Length == 0)
                return;

            var EntrySize = (int)((TableEntrySize > 0) ? TableEntrySize : TableEntryV4Size);
            var Count = Math.Min(TableEntryNum, (uint)(Table.Length / EntrySize));
            for (var I = 0; I < Count; I++)
            {
                var Offset = I * EntrySize;
                var Entry = new Entry
                {
                    Size = ReadU32(Table, Offset + 0),
                    Offset = ReadU32(Table, Offset + 4),
                    Type = ReadU32(Table, Offset + 8),
                    Name = ReadString(Table, Offset + 12, 32),
                    BoardId = new uint[16]
                };
                for (var B = 0; B < 16; B++)
                    Entry.BoardId[B] = ReadU32(Table, Offset + 44 + B * 4);

                Entries.Add(Entry);
            }
        }

        public byte[] VendorRamdisk =>
            (HeaderVersion >= 4) ? new byte[0] : ReadArray(VendorRamdiskOffset, VendorRamdiskSize);

        public byte[] Dtb => ReadArray(DtbOffset, DtbSize);

        public byte[] Bootconfig => (HeaderVersion >= 4) ? ReadArray(BootconfigOffset, BootconfigSize) : new byte[0];

        public byte[] GetEntryData(Entry Entry) => ReadArray(Entry.FileOffset(VendorRamdiskOffset), Entry.Size);

        public byte[] ReadArray(long Offset, long Length)
        {
            if ((Offset < 0) || (Length <= 0) || (Offset + Length > Raw.Length))
                return new byte[0];

            var Result = new byte[Length];
            Buffer.BlockCopy(Raw, (int)Offset, Result, 0, (int)Length);
            return Result;
        }

        private static long Align(long Value, long Alignment) =>
            ((Value + Alignment - 1) / Alignment) * Alignment;

        private uint ReadU32(long Offset) => ReadU32(Raw, Offset);

        private ulong ReadU64(long Offset) =>
            ReadU32(Raw, Offset) | ((ulong)ReadU32(Raw, Offset + 4) << 32);

        private static uint ReadU32(byte[] Data, long Offset)
        {
            if ((Offset < 0) || (Offset + 4 > Data.Length))
                return 0;

            return (uint)(Data[Offset] | (Data[Offset + 1] << 8) | (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));
        }

        private string ReadString(long Offset, int Length) => ReadString(Raw, Offset, Length);

        private static string ReadString(byte[] Data, long Offset, int Length)
        {
            if ((Offset < 0) || (Offset >= Data.Length))
                return "";

            var End = (int)Math.Min(Data.Length, Offset + Length);
            var Count = 0;
            while ((Offset + Count < End) && (Data[Offset + Count] != 0))
                Count++;

            return Encoding.ASCII.GetString(Data, (int)Offset, Count);
        }
    }
}
