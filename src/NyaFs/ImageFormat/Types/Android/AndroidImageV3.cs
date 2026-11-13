using System;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Types.Android
{
    /// <summary>
    /// Android boot image header v3 (AOSP boot_img_hdr_v3). The page size is fixed at 4096
    /// and there is no second stage/recovery DTBO/DTB field in the boot image.
    /// </summary>
    class AndroidImageV3 : RawPacket
    {
        public const int PageSize = 0x1000;
        public const int HeaderSize = 1580;

        public AndroidImageV3(byte[] Raw) : base(Raw)
        {
        }

        public bool IsMagicCorrect =>
            ReadString(0, 8) == "ANDROID!" && HeaderVersion == 3;

        public uint KernelSize
        {
            get => ReadUInt32(0x08);
            set => WriteUInt32(0x08, value);
        }

        public uint RamdiskSize
        {
            get => ReadUInt32(0x0C);
            set => WriteUInt32(0x0C, value);
        }

        public uint OSVersion
        {
            get => ReadUInt32(0x10);
            set => WriteUInt32(0x10, value);
        }

        public uint HeaderSizeValue
        {
            get => ReadUInt32(0x14);
            set => WriteUInt32(0x14, value);
        }

        public uint HeaderVersion
        {
            get => ReadUInt32(0x28);
            set => WriteUInt32(0x28, value);
        }

        public string Cmdline
        {
            get => ReadString(0x2C, 0x600);
            set => WriteString(0x2C, value ?? "", 0x600);
        }

        public long KernelOffset => PageSize;
        public long RamdiskOffset => Align(KernelOffset + KernelSize, PageSize);

        public byte[] Kernel => ReadArray(KernelOffset, KernelSize);
        public byte[] Ramdisk => ReadArray(RamdiskOffset, RamdiskSize);

        private static long Align(long Value, long Alignment) =>
            ((Value + Alignment - 1) / Alignment) * Alignment;
    }
}
