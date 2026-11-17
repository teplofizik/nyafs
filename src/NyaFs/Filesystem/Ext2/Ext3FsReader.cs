using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Ext2
{
    /// <summary>
    /// ext3 filesystem reader. ext3 is ext2 plus a journal; the block addressing is identical,
    /// so all reading logic is inherited from <see cref="Ext2FsReader"/> (the journal is ignored).
    /// </summary>
    public class Ext3FsReader : Ext2FsReader
    {
        public Ext3FsReader(byte[] Data) : base(Data) { }

        public Ext3FsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }
    }
}
