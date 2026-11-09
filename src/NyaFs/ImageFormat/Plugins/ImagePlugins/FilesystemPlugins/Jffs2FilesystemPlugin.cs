using NyaFs.ImageFormat.Elements.Fs;
using System;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    /// <summary>JFFS2 raw MTD image reader/writer.</summary>
    class Jffs2FilesystemPlugin : Base.FilesystemPlugin
    {
        public Jffs2FilesystemPlugin() : base("fs.jffs2", "jffs2", true, true, true)
        {
        }

        public override NyaFs.ImageFormat.Types.FsType FsType => NyaFs.ImageFormat.Types.FsType.Jffs2;

        public override bool DetectFilesystem(ImageData Image) =>
            Filesystem.Jffs2.Jffs2Reader.IsJffs2(Image.Raw);

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.Jffs2, new Filesystem.Jffs2.Jffs2Reader(Data));

        public override Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.Jffs2Writer();
    }
}
