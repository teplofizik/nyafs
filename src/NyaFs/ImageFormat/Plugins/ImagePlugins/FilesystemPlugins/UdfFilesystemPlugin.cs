using NyaFs.ImageFormat.Elements.Fs;
using System;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class UdfFilesystemPlugin : Base.FilesystemPlugin
    {
        public UdfFilesystemPlugin() : base("fs.udf", "udf", true, true, true)
        {
        }

        public override bool DetectFilesystem(ImageData Image) =>
            Filesystem.Udf.UdfReader.IsUdfImage(Image.Raw);

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.Udf, new Filesystem.Udf.UdfReader(Data));

        public override Elements.Fs.Writer.Writer GetFilesystemWriter(LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.UdfWriter(Fs);

        public override bool CheckFilesystem(LinuxFilesystem Fs) => true;
    }
}
