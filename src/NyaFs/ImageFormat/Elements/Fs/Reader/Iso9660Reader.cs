using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Reader
{
    public class Iso9660Reader : BaseFsReader
    {
        public Iso9660Reader(string Filename) : this(File.ReadAllBytes(Filename))
        {
        }

        public Iso9660Reader(byte[] Data) : base(Types.FsType.Iso9660, new Filesystem.Iso9660.Iso9660Reader(Data))
        {
        }
    }
}
