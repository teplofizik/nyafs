using NyaFs.ImageFormat.Elements.Fs;
using System;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    /// <summary>
    /// YAFFS2 raw NAND reader. YAFFS2 has no superblock magic, so detection
    /// scores the standard page/OOB candidates against YAFFS2 tags.
    /// </summary>
    class Yaffs2FilesystemPlugin : Base.FilesystemPlugin
    {
        public Yaffs2FilesystemPlugin() : base("fs.yaffs2", "yaffs2", true, true, true)
        {
        }

        public override NyaFs.ImageFormat.Types.FsType FsType => NyaFs.ImageFormat.Types.FsType.Yaffs2;

        public override bool DetectFilesystem(ImageData Image) =>
            Filesystem.Yaffs2.Yaffs2Reader.IsYaffs2(Image.Raw);

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.Yaffs2, new Filesystem.Yaffs2.Yaffs2Reader(Data));

        public override Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.Yaffs2Writer();
    }
}
