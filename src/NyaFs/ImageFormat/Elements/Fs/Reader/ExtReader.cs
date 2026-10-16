using System.IO;

namespace NyaFs.ImageFormat.Elements.Fs.Reader
{
    public class ExtReader : BaseFsReader
    {
        public ExtReader(string Filename) : this(File.ReadAllBytes(Filename)) { }

        public ExtReader(byte[] data) : base(Types.FsType.Ext2, new Filesystem.Ext2.Ext2FsReader(data)) { }

        public ExtReader(Types.FsType Type, byte[] data) : base(Type, Create(Type, data)) { }

        private static Filesystem.Universal.IFilesystemReader Create(Types.FsType Type, byte[] data)
        {
            switch (Type)
            {
                case Types.FsType.Ext3: return new Filesystem.Ext2.Ext3FsReader(data);
                case Types.FsType.Ext4: return new Filesystem.Ext2.Ext4FsReader(data);
                default: return new Filesystem.Ext2.Ext2FsReader(data);
            }
        }
    }
}
