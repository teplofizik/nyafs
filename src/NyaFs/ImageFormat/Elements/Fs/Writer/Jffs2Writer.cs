namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class Jffs2Writer : BaseFsWriter
    {
        public Jffs2Writer() : base(new Filesystem.Jffs2.Jffs2Builder()) { }

        public Jffs2Writer(LinuxFilesystem Fs) : base(new Filesystem.Jffs2.Jffs2Builder(Fs.Jffs2EraseBlockSize, Fs.Jffs2PageSize, Fs.Jffs2Compression)) { }
    }
}
