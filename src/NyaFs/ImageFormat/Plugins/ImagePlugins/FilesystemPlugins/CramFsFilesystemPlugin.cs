using NyaFs.ImageFormat.Elements.Fs;
using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class CramFsFilesystemPlugin : Base.FilesystemPlugin
    {
        public CramFsFilesystemPlugin() : base("fs.cramfs", "cramfs", true, true, true)
        {

        }

        /// <summary>
        /// Is image contain this filesystem
        /// </summary>
        /// <param name="Image"></param>
        /// <returns></returns>
        public override bool DetectFilesystem(ImageData Image) =>
            (Image.Length >= 4) && ((Image.ReadUInt32BE(0) == 0x453dcd28u) || (Image.ReadUInt32BE(0) == 0x28cd3d45u));

        /// <summary>
        /// Get reader
        /// </summary>
        /// <returns></returns>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.CramFs, new Filesystem.CramFs.CramFsReader(Data));

        /// <summary>
        /// Get writer
        /// </summary>
        /// <returns></returns>
        public override Elements.Fs.Writer.Writer GetFilesystemWriter(LinuxFilesystem Fs) =>
            new Elements.Fs.Writer.BaseFsWriter(new Filesystem.CramFs.CramFsBuilder(Fs.CramFsBigEndian));

        public override bool CheckFilesystem(LinuxFilesystem Fs)
        {
            // cramfs: gid is 8-bit, uid is 16-bit, dirent name length is 6 bits in 4-byte units (<= 252).
            return CheckUidGid(Fs, 0xFFFF, 0xFF) && CheckNameLength(Fs, 252);
        }
    }
}
