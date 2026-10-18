using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class F2fsFilesystemPlugin : Base.FilesystemPlugin
    {
        public F2fsFilesystemPlugin() : base("fs.f2fs", "f2fs", true, true, false)
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

        /// <summary>
        /// Get writer (total size from Fs.FsSize or 64 MiB default)
        /// </summary>
        public override Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs)
        {
            long Size = (Fs.FsSize > 0) ? Fs.FsSize : 64L * 1024 * 1024;
            return new Elements.Fs.Writer.BaseFsWriter(new Filesystem.F2fs.F2fsFsBuilder(Size));
        }

        /// <summary>Milestone 1: empty filesystem only (mkfs-like image without user files).</summary>
        public override bool CheckFilesystem(Elements.Fs.LinuxFilesystem Fs)
        {
            if (Fs.Fs.Root.Items.Count > 0)
            {
                Log.Error(0, "F2FS writer currently supports only an empty filesystem (Milestone 1)");
                return false;
            }

            return true;
        }
    }
}
