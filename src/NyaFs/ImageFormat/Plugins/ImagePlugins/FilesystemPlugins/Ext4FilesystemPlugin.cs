using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class Ext4FilesystemPlugin : Base.FilesystemPlugin
    {
        public Ext4FilesystemPlugin() : base("fs.ext4", "ext4", true, false, false)
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
    }
}
