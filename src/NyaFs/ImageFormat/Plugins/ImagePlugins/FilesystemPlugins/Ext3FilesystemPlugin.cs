using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class Ext3FilesystemPlugin : Base.FilesystemPlugin
    {
        public Ext3FilesystemPlugin() : base("fs.ext3", "ext3", true, true, false)
        {

        }

        /// <summary>
        /// Is image contain this filesystem (ext with a journal, no ext4-only features)
        /// </summary>
        public override bool DetectFilesystem(ImageData Image) => ExtDetector.IsExt3(Image);

        /// <summary>
        /// Get reader
        /// </summary>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.Ext3, new Filesystem.Ext2.Ext3FsReader(Data));

        /// <summary>
        /// Get writer (ext2 layout + a journal generated from scratch)
        /// </summary>
        public override Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.BaseFsWriter(new Filesystem.Ext2.Ext3FsBuilder(DetectFixDiskSize(Fs)));

        private uint DetectFixDiskSize(Elements.Fs.LinuxFilesystem Fs) =>
            Convert.ToUInt32(Fs.GetContentSize() * 2 + 0x100000).GetAligned(0x800000);

        /// <summary>ext3 stores uid/gid in 16-bit fields.</summary>
        public override bool CheckFilesystem(Elements.Fs.LinuxFilesystem Fs) => CheckUidGid(Fs, 0xFFFF, 0xFFFF);
    }
}
