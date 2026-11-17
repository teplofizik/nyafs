using System;
namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class Jffs2Writer : BaseFsWriter
    {
        public Jffs2Writer() : base(new Filesystem.Jffs2.Jffs2Builder()) { }

        public Jffs2Writer(LinuxFilesystem Fs) : base(CreateBuilder(Fs)) { }

        private static Filesystem.Jffs2.Jffs2Builder CreateBuilder(LinuxFilesystem Fs)
        {
            var Builder = new Filesystem.Jffs2.Jffs2Builder(Fs.Jffs2EraseBlockSize, Fs.Jffs2PageSize, Fs.Jffs2Compression);
            string Cleanmarkers = Fs.GetFilesystemOption("jffs2", "cleanmarkers", null);
            if (Cleanmarkers != null)
                Builder.Cleanmarkers = !string.Equals(Cleanmarkers, "false", StringComparison.OrdinalIgnoreCase);
            return Builder;
        }
    }
}
