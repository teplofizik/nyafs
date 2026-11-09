namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class Yaffs2Writer : BaseFsWriter
    {
        public Yaffs2Writer() : base(new Filesystem.Yaffs2.Yaffs2Builder()) { }

        public Yaffs2Writer(Filesystem.Yaffs2.Yaffs2Builder Builder) : base(Builder) { }
    }
}
