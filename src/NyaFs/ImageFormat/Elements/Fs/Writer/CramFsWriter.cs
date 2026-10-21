using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class CramFsWriter : BaseFsWriter
    {
        public CramFsWriter() : this(false) { }

        public CramFsWriter(bool BigEndian) : base(new Filesystem.CramFs.CramFsBuilder(BigEndian)) { }

        public CramFsWriter(string Filename) : this(false, Filename) { }

        public CramFsWriter(bool BigEndian, string Filename) :
            base(new Filesystem.CramFs.CramFsBuilder(BigEndian), Filename) { }
    }
}
