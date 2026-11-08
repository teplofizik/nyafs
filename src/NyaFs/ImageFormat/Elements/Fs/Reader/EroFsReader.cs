using System;
using System.IO;

namespace NyaFs.ImageFormat.Elements.Fs.Reader
{
    public class EroFsReader : BaseFsReader
    {
        public EroFsReader(string Filename) : this(File.ReadAllBytes(Filename))
        {
        }

        public EroFsReader(byte[] Data) : this(Data, new Filesystem.EroFs.EroFsReader(Data))
        {
        }

        private EroFsReader(byte[] Data, Filesystem.EroFs.EroFsReader Reader) :
            base(Types.FsType.EroFs, Reader, Reader.VolumeBytes)
        {
        }
    }
}
