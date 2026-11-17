using System;
using System.IO;

namespace NyaFs.ImageFormat.Elements.Fs.Reader
{
    public class ExFatReader : BaseFsReader
    {
        public ExFatReader(string Filename) : this(File.ReadAllBytes(Filename))
        {
        }

        public ExFatReader(byte[] Data) : this(Data, new Filesystem.ExFat.ExFatReader(Data))
        {
        }

        private ExFatReader(byte[] Data, Filesystem.ExFat.ExFatReader Reader) :
            base(Types.FsType.ExFat, Reader, Reader.VolumeBytes)
        {
        }
    }
}
