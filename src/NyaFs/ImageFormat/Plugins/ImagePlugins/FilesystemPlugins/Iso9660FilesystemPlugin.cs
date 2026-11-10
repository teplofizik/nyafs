using NyaFs.ImageFormat.Elements.Fs;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class Iso9660FilesystemPlugin : Base.FilesystemPlugin
    {
        public override NyaFs.ImageFormat.Types.FsType FsType => NyaFs.ImageFormat.Types.FsType.Iso9660;

        public override string[] Aliases => new[] { "iso" };
        public override string[] Extensions => new[] { "iso", "iso9660" };

        public Iso9660FilesystemPlugin() : base("fs.iso9660", "iso9660", true, true, true)
        {
        }

        public override bool DetectFilesystem(ImageData Image) =>
            (Image.Length >= 0x8006) &&
            (Image.ReadByte(0x8001) == (byte)'C') && (Image.ReadByte(0x8002) == (byte)'D') &&
            (Image.ReadByte(0x8003) == (byte)'0') && (Image.ReadByte(0x8004) == (byte)'0') &&
            (Image.ReadByte(0x8005) == (byte)'1');

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.Iso9660, new Filesystem.Iso9660.Iso9660Reader(Data));

        public override Elements.Fs.Writer.Writer GetFilesystemWriter(LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.BaseFsWriter(new Filesystem.Iso9660.Iso9660Builder());

        public override bool CheckFilesystem(LinuxFilesystem Fs) => true;
    }
}
