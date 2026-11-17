using NyaFs.ImageFormat.Elements.Fs;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class ExFatFilesystemPlugin : Base.FilesystemPlugin
    {
        public override NyaFs.ImageFormat.Types.FsType FsType => NyaFs.ImageFormat.Types.FsType.ExFat;

        public ExFatFilesystemPlugin() : base("fs.exfat", "exfat", true, true, true)
        {
        }

        public override bool DetectFilesystem(ImageData Image) =>
            (Image.Length >= 512) && Filesystem.ExFat.ExFatShared.IsExFat(Image.Raw, (int)Image.Offset);

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.ExFat, new Filesystem.ExFat.ExFatReader(Data));

        public override Elements.Fs.Writer.Writer GetFilesystemWriter(LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.ExFatWriter(Elements.Fs.Writer.ExFatWriter.GetSize(Fs));

        public override bool CheckFilesystem(LinuxFilesystem Fs) => true;
    }
}
