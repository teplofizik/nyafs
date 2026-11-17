using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class EroFsWriter : BaseFsWriter
    {
        public EroFsWriter() : base(new Filesystem.EroFs.EroFsBuilder()) { }

        public EroFsWriter(string Filename) : base(new Filesystem.EroFs.EroFsBuilder(), Filename) { }

        public EroFsWriter(LinuxFilesystem Fs) : base(CreateBuilder(Fs)) { }

        public EroFsWriter(LinuxFilesystem Fs, string Filename) : base(CreateBuilder(Fs), Filename) { }

        private static Filesystem.EroFs.EroFsBuilder CreateBuilder(LinuxFilesystem Fs)
        {
            var Builder = new Filesystem.EroFs.EroFsBuilder
            {
                Fragments = Fs.EroFsFragments,
                TailFragments = Fs.EroFsTailFragments,
                SharedXattrs = Fs.EroFsSharedXattrs,
                CompressionLevel = Fs.EroFsCompressionLevel
            };

            foreach (var Prefix in Fs.EroFsXattrPrefixes)
                Builder.AddXattrPrefix(Prefix);

            return Builder;
        }
    }
}
