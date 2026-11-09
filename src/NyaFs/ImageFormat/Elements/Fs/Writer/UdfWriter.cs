using System;

namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class UdfWriter : BaseFsWriter
    {
        public UdfWriter() : base(new Filesystem.Udf.UdfBuilder()) { }

        public UdfWriter(string Filename) : base(new Filesystem.Udf.UdfBuilder(), Filename) { }
    }
}
