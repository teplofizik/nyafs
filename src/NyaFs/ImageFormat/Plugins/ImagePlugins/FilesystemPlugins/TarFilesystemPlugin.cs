using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class TarFilesystemPlugin : Base.FilesystemPlugin
    {
        public TarFilesystemPlugin() : base("fs.tar", "tar", true, true, false)
        {

        }

        /// <summary>
        /// Is image contain this filesystem (ustar magic at offset 257)
        /// </summary>
        public override bool DetectFilesystem(ImageData Image) =>
            (Image.Length >= 263) &&
            (Image.ReadByte(257) == (byte)'u') &&
            (Image.ReadByte(258) == (byte)'s') &&
            (Image.ReadByte(259) == (byte)'t') &&
            (Image.ReadByte(260) == (byte)'a') &&
            (Image.ReadByte(261) == (byte)'r');

        /// <summary>
        /// Get reader
        /// </summary>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.Tar, new Filesystem.Tar.TarFsReader(Data));

        /// <summary>
        /// Get writer
        /// </summary>
        public override Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.BaseFsWriter(new Filesystem.Tar.TarFsBuilder());
    }
}
