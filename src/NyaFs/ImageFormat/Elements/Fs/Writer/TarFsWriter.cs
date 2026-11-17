using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class TarFsWriter : BaseFsWriter
    {
        public TarFsWriter() : base(new Filesystem.Tar.TarFsBuilder()) { }

        public TarFsWriter(string Filename) : base(new Filesystem.Tar.TarFsBuilder(), Filename) { }
    }
}
