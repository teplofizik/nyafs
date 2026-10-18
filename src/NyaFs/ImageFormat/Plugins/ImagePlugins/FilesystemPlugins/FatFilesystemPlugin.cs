using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class FatFilesystemPlugin : Base.FilesystemPlugin
    {
        public FatFilesystemPlugin() : base("fs.fat", "fat", true, true, false)
        {

        }

        /// <summary>
        /// Is image a FAT12/FAT16/FAT32 volume (valid BPB).
        /// </summary>
        public override bool DetectFilesystem(ImageData Image) =>
            Filesystem.Fat.FatDetector.IsFat(Image.Raw, Image.Offset);

        /// <summary>
        /// Get reader
        /// </summary>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.BaseFsReader(Types.FsType.Fat, new Filesystem.Fat.FatFsReader(Data));

        /// <summary>
        /// Get writer (target FAT type from Fs.FatType, total size from Fs.FsSize or estimated)
        /// </summary>
        public override Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs)
        {
            long Size = (Fs.FsSize > 0) ? Fs.FsSize : DetectSize(Fs);
            return new Elements.Fs.Writer.BaseFsWriter(new Filesystem.Fat.FatFsBuilder(Size, Fs.FatType));
        }

        /// <summary>FAT only stores files and directories; owners/modes are not representable.</summary>
        public override bool CheckFilesystem(Elements.Fs.LinuxFilesystem Fs)
        {
            string Bad = null;

            IterateAllFilesystem(Fs, I =>
            {
                if (Bad == null)
                {
                    var T = I.ItemType;
                    if ((T != Filesystem.Universal.Types.FilesystemItemType.File) &&
                        (T != Filesystem.Universal.Types.FilesystemItemType.Directory))
                        Bad = $"{T} {I.Filename}";
                }
            });

            if (Bad != null)
            {
                Log.Error(0, $"{Bad} cannot be saved in a FAT image (only files and directories are supported)");
                return false;
            }

            return true;
        }

        private static long DetectSize(Elements.Fs.LinuxFilesystem Fs)
        {
            long Content = Fs.GetContentSize();
            long Size = Math.Max(1440 * 1024, Content * 2 + 1024 * 1024);
            return (Size / 512) * 512;
        }
    }
}
