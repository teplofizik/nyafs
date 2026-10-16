using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class Ext3FilesystemPlugin : Base.FilesystemPlugin
    {
        public Ext3FilesystemPlugin() : base("fs.ext3", "ext3", true, false, false)
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
    }
}
