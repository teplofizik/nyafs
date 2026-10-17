using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Types
{
    public enum FsType
    {
        Unknown,
        Cpio,
        Ext2,
        Ext3,
        Ext4,
        SquashFs,
        CramFs,
        RomFs,
        Tar,
        LittleFs,
        Spiffs,
        UbiFs
    }
}
