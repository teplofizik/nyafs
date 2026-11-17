using System;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Types.Android
{
    /// <summary>
    /// Android boot image header v3/v4 (AOSP boot_img_hdr_v3/v4). The page size is
    /// fixed at 4096; v4 adds a boot signature appended after the ramdisk.
    /// </summary>
    class AndroidImageV3 : RawPacket
    {
        public const int PageSize = 0x1000;
        public const int HeaderSizeV3 = 1580;
        public const int HeaderSizeV4 = 1584;
        public const int SignatureSizeField = 1580;

        private readonly byte[] Source;

        public AndroidImageV3(byte[] Raw) : base(Raw)
        {
            Source = Raw;
        }

        public bool IsMagicCorrect =>
            ReadString(0, 8) == "ANDROID!" && (HeaderVersion == 3 || HeaderVersion == 4);

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

        public uint SignatureSize =>
            (HeaderVersion >= 4) ? ReadUInt32(SignatureSizeField) : 0;

        public long KernelOffset => PageSize;
        public long RamdiskOffset => Align(KernelOffset + KernelSize, PageSize);
        public long SignatureOffset => Align(RamdiskOffset + RamdiskSize, PageSize);

        public byte[] Kernel => ReadArray(KernelOffset, KernelSize);
        public byte[] Ramdisk => ReadArray(RamdiskOffset, RamdiskSize);
        public byte[] Signature
        {
            get
            {
                var Size = SignatureSize;
                if (Size == 0)
                    return new byte[0];
                if ((SignatureOffset + Size) > Source.Length)
                    return new byte[0];
                return ReadArray(SignatureOffset, Size);
            }
        }

        private static long Align(long Value, long Alignment) =>
            ((Value + Alignment - 1) / Alignment) * Alignment;
    }
}
