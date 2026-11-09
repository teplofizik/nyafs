namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class Jffs2Writer : BaseFsWriter
    {
        public Jffs2Writer() : base(new Filesystem.Jffs2.Jffs2Builder()) { }
    }
}
