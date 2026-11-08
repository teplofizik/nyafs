using NyaFs.ImageFormat.Elements.Fs;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class EroFsFilesystemPlugin : Base.FilesystemPlugin
    {
        public EroFsFilesystemPlugin() : base("fs.erofs", "erofs", true, false, true)
        {
        }

        public override bool DetectFilesystem(ImageData Image) =>
            (Image.Length >= 1028) && (Image.ReadUInt32(1024) == 0xE0F5E1E2u);

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.EroFs, new Filesystem.EroFs.EroFsReader(Data));

        public override Elements.Fs.Writer.Writer GetFilesystemWriter(LinuxFilesystem Fs) => null;

        public override bool CheckFilesystem(LinuxFilesystem Fs) => true;
    }
}
