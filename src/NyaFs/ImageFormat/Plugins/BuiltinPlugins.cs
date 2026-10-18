using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.ImageFormat.Plugins.CompressorPlugins;
using NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins;
using NyaFs.ImageFormat.Plugins.ImagePlugins.KernelPlugins;

namespace NyaFs.ImageFormat.Plugins
{
    /// <summary>
    /// Built-in plugins, registered by default by <see cref="Processor.ImageProcessor"/>.
    /// Kept in one place so detection/loading logic lives in the plugins, not in central switches.
    /// </summary>
    internal static class BuiltinPlugins
    {
        public static readonly Base.CompositeImagePlugin[] Images = new Base.CompositeImagePlugin[]
        {
            new ImagePlugins.CompositePlugins.AndroidPlugin(),
            new ImagePlugins.CompositeImagePlugins.FitPlugin(),
            new ImagePlugins.CompositeImagePlugins.LegacyImagePlugin()
        };

        public static readonly Base.FilesystemPlugin[] Filesystems = new Base.FilesystemPlugin[]
        {
            new CpioFilesystemPlugin(),
            new CramFsFilesystemPlugin(),
            new Ext4FilesystemPlugin(),
            new Ext3FilesystemPlugin(),
            new Ext2FilesystemPlugin(),
            new RomFsFilesystemPlugin(),
            new SquashFsFilesystemPlugin(),
            new CImgFilesystemPlugin(),
            new TarFilesystemPlugin(),
            new UbifsFilesystemPlugin(),
            new FatFilesystemPlugin(),
            new F2fsFilesystemPlugin()
        };

        public static readonly Base.KernelPlugin[] Kernels = new Base.KernelPlugin[]
        {
            new ZImageKernelPlugin(),
            new RawKernelPlugin()
        };

        public static readonly Base.DevtreePlugin[] Dtbs = new Base.DevtreePlugin[]
        {
            new ImagePlugins.DtbPlugins.DtbPlugin(),
            new ImagePlugins.DtbPlugins.DtsPlugin()
        };

        public static readonly Base.CompressorPlugin[] Compressors = new Base.CompressorPlugin[]
        {
            new GzipCompressorPlugin(),
            new Bz2CompressorPlugin(),
            new Lz4CompressorPlugin(),
            new LzmaCompressorPlugin(),
            new LzoCompressorPlugin(),
            new XzCompressorPlugin(),
            new ZStdCompressorPlugin()
        };
    }
}
