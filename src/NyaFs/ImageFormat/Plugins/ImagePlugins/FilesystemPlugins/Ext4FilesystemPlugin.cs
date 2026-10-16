using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class Ext4FilesystemPlugin : Base.FilesystemPlugin
    {
        public Ext4FilesystemPlugin() : base("fs.ext4", "ext4", true, true, false)
        {

        }

        /// <summary>
        /// Is image contain this filesystem (ext with any ext4-only feature, e.g. extents)
        /// </summary>
        public override bool DetectFilesystem(ImageData Image) => ExtDetector.IsExt4(Image);

        /// <summary>
        /// Get reader
        /// </summary>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.Ext4, new Filesystem.Ext2.Ext4FsReader(Data));

        /// <summary>
        /// Get writer (ext3 layout + extents)
        /// </summary>
        public override Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.BaseFsWriter(new Filesystem.Ext2.Ext4FsBuilder(DetectFixDiskSize(Fs)));

        private uint DetectFixDiskSize(Elements.Fs.LinuxFilesystem Fs) =>
            Convert.ToUInt32(Fs.GetContentSize() * 2 + 0x100000).GetAligned(0x800000);
    }
}
