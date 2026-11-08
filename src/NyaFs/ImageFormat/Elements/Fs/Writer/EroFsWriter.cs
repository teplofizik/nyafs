using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class EroFsWriter : BaseFsWriter
    {
        public EroFsWriter() : base(new Filesystem.EroFs.EroFsBuilder()) { }

        public EroFsWriter(string Filename) : base(new Filesystem.EroFs.EroFsBuilder(), Filename) { }
    }
}
