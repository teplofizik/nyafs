using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Tar.Types
{
    /// <summary>
    /// Tar header typeflag values.
    /// </summary>
    public enum TarFileType : byte
    {
        /// <summary>'0' or '\0' - regular file</summary>
        Regular = (byte)'0',

        /// <summary>'1' - hard link</summary>
        HardLink = (byte)'1',

        /// <summary>'2' - symbolic link</summary>
        SymLink = (byte)'2',

        /// <summary>'3' - character device</summary>
        Character = (byte)'3',

        /// <summary>'4' - block device</summary>
        Block = (byte)'4',

        /// <summary>'5' - directory</summary>
        Directory = (byte)'5',

        /// <summary>'6' - fifo</summary>
        Fifo = (byte)'6',

        /// <summary>'7' - contiguous file</summary>
        Contiguous = (byte)'7',

        /// <summary>'L' - GNU long name (name is in data block)</summary>
        GnuLongName = (byte)'L',

        /// <summary>'K' - GNU long link (link name is in data block)</summary>
        GnuLongLink = (byte)'K',

        /// <summary>'S' - GNU sparse file</summary>
        GnuSparse = (byte)'S'
    }
}
