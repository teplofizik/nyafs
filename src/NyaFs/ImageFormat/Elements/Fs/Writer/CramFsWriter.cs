using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class CramFsWriter : BaseFsWriter
    {
        public CramFsWriter() : this(false) { }

        public CramFsWriter(bool BigEndian) : this(BigEndian, false) { }

        public CramFsWriter(bool BigEndian, bool WideInodes) : base(new Filesystem.CramFs.CramFsBuilder(BigEndian, WideInodes)) { }

        public CramFsWriter(string Filename) : this(false, Filename) { }

        public CramFsWriter(bool BigEndian, string Filename) :
            base(new Filesystem.CramFs.CramFsBuilder(BigEndian), Filename) { }

        public CramFsWriter(bool BigEndian, bool WideInodes, string Filename) :
            base(new Filesystem.CramFs.CramFsBuilder(BigEndian, WideInodes), Filename) { }
    }
}
