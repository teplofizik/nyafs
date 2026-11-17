using NyaFs.Filesystem.Universal;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class ExFatWriter : BaseFsWriter
    {
        public ExFatWriter(long Size) : base(new Filesystem.ExFat.ExFatBuilder(Size))
        {
        }

        public ExFatWriter(long Size, string Filename) : base(new Filesystem.ExFat.ExFatBuilder(Size), Filename)
        {
        }

        public static long GetSize(LinuxFilesystem Fs)
        {
            long Size = (Fs != null && Fs.FsSize > 0)
                ? Fs.FsSize
                : Math.Max(64L * 1024 * 1024, (Fs?.GetContentSize() ?? 0) * 2 + 16L * 1024 * 1024);

            long Alignment = 512L * 8;
            return ((Size + Alignment - 1) / Alignment) * Alignment;
        }
    }
}
