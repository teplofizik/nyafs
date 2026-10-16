using NyaFs.ImageFormat;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    /// <summary>
    /// Shared ext2/ext3/ext4 detection: the superblock magic plus the feature bits that
    /// distinguish the three variants (ext3 = journal, ext4 = any ext4-only feature).
    /// </summary>
    internal static class ExtDetector
    {
        const uint SUPERBLOCK_OFFSET = 0x400;

        // s_feature_compat
        const uint COMPAT_HAS_JOURNAL = 0x4;

        // s_feature_incompat
        const uint INCOMPAT_EXTENTS = 0x40;
        const uint INCOMPAT_64BIT = 0x80;
        const uint INCOMPAT_MMP = 0x100;
        const uint INCOMPAT_FLEX_BG = 0x200;
        const uint INCOMPAT_EA_INODE = 0x400;
        const uint INCOMPAT_DIRDATA = 0x1000;
        const uint INCOMPAT_CSUM_SEED = 0x2000;
        const uint INCOMPAT_LARGEDIR = 0x4000;
        const uint INCOMPAT_INLINE_DATA = 0x8000;
        const uint INCOMPAT_ENCRYPT = 0x10000;

        // s_feature_ro_compat
        const uint RO_COMPAT_HUGE_FILE = 0x8;
        const uint RO_COMPAT_GDT_CSUM = 0x10;
        const uint RO_COMPAT_DIR_NLINK = 0x20;
        const uint RO_COMPAT_EXTRA_ISIZE = 0x40;
        const uint RO_COMPAT_QUOTA = 0x100;
        const uint RO_COMPAT_BIGALLOC = 0x200;
        const uint RO_COMPAT_METADATA_CSUM = 0x400;
        const uint RO_COMPAT_PROJECT = 0x2000;

        const uint EXT4_INCOMPAT = INCOMPAT_EXTENTS | INCOMPAT_64BIT | INCOMPAT_MMP | INCOMPAT_FLEX_BG |
            INCOMPAT_EA_INODE | INCOMPAT_DIRDATA | INCOMPAT_CSUM_SEED | INCOMPAT_LARGEDIR |
            INCOMPAT_INLINE_DATA | INCOMPAT_ENCRYPT;

        const uint EXT4_RO_COMPAT = RO_COMPAT_HUGE_FILE | RO_COMPAT_GDT_CSUM | RO_COMPAT_DIR_NLINK |
            RO_COMPAT_EXTRA_ISIZE | RO_COMPAT_QUOTA | RO_COMPAT_BIGALLOC | RO_COMPAT_METADATA_CSUM |
            RO_COMPAT_PROJECT;

        public static bool IsExt(ImageData Image) =>
            (Image.Length >= SUPERBLOCK_OFFSET + 0x3A) && (Image.ReadUInt16(SUPERBLOCK_OFFSET + 0x38) == 0xEF53);

        public static bool HasJournal(ImageData Image) =>
            IsExt(Image) && ((Image.ReadUInt32(SUPERBLOCK_OFFSET + 0x5C) & COMPAT_HAS_JOURNAL) != 0);

        public static bool HasExt4Features(ImageData Image) =>
            IsExt(Image) &&
            (((Image.ReadUInt32(SUPERBLOCK_OFFSET + 0x60) & EXT4_INCOMPAT) != 0) ||
             ((Image.ReadUInt32(SUPERBLOCK_OFFSET + 0x64) & EXT4_RO_COMPAT) != 0));

        public static bool IsExt2(ImageData Image) => IsExt(Image) && !HasExt4Features(Image) && !HasJournal(Image);

        public static bool IsExt3(ImageData Image) => IsExt(Image) && !HasExt4Features(Image) && HasJournal(Image);

        public static bool IsExt4(ImageData Image) => IsExt(Image) && HasExt4Features(Image);
    }
}
