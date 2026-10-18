using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class F2fsFilesystemPlugin : Base.FilesystemPlugin
    {
        public F2fsFilesystemPlugin() : base("fs.f2fs", "f2fs", true, false, false)
        {

        }

        /// <summary>
        /// Is image an F2FS volume (superblock magic at offset 1024).
        /// </summary>
        public override bool DetectFilesystem(ImageData Image) =>
            Filesystem.F2fs.F2fsDetector.IsF2fs(Image.Raw, Image.Offset);

        /// <summary>
        /// Get reader
        /// </summary>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.F2fs, new Filesystem.F2fs.F2fsFsReader(Data));
    }
}
