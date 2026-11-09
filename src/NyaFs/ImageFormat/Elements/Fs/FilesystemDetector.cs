using System;
using System.Collections.Generic;
using NyaFs.ImageFormat.Plugins;
using NyaFs.ImageFormat.Plugins.Base;

namespace NyaFs.ImageFormat.Elements.Fs
{
    /// <summary>
    /// Compatibility entry point for filesystem detection.
    /// Detection itself lives in <see cref="FilesystemPlugin.DetectFilesystem"/>:
    /// this class only asks the registered plugins in priority/registration order.
    /// </summary>
    public static class FilesystemDetector
    {
        /// <summary>
        /// Detect a filesystem using the built-in plugin registry.
        /// </summary>
        public static Types.FsType DetectFs(byte[] Raw) =>
            DetectFs(new ImageData(Raw), BuiltinPlugins.Filesystems);

        /// <summary>
        /// Detect a filesystem using an explicit plugin list (for external/custom plugins).
        /// </summary>
        public static Types.FsType DetectFs(byte[] Raw, IEnumerable<FilesystemPlugin> Plugins) =>
            DetectFs(new ImageData(Raw), Plugins);

        /// <summary>
        /// Detect a filesystem in an image range using an explicit plugin list.
        /// </summary>
        public static Types.FsType DetectFs(ImageData Data, IEnumerable<FilesystemPlugin> Plugins)
        {
            var Plugin = DetectFilesystemPlugin(Data, Plugins);
            return Plugin?.FsType ?? Types.FsType.Unknown;
        }

        /// <summary>
        /// Find the first plugin that recognizes the data. Higher-priority plugins are probed first.
        /// </summary>
        public static FilesystemPlugin DetectFilesystemPlugin(byte[] Raw) =>
            DetectFilesystemPlugin(new ImageData(Raw), BuiltinPlugins.Filesystems);

        /// <summary>
        /// Find the first plugin that recognizes the data using an explicit list.
        /// </summary>
        public static FilesystemPlugin DetectFilesystemPlugin(byte[] Raw, IEnumerable<FilesystemPlugin> Plugins) =>
            DetectFilesystemPlugin(new ImageData(Raw), Plugins);

        /// <summary>
        /// Find the first plugin that recognizes the image range.
        /// </summary>
        public static FilesystemPlugin DetectFilesystemPlugin(ImageData Data, IEnumerable<FilesystemPlugin> Plugins)
        {
            if ((Data == null) || (Plugins == null))
                return null;

            // Keep registration order for equal priorities; stable sort by descending priority.
            var Ordered = new List<FilesystemPlugin>(Plugins);
            Ordered.Sort((A, B) => B.Priority.CompareTo(A.Priority));

            foreach (var Plugin in Ordered)
            {
                try
                {
                    if (Plugin.DetectFilesystem(Data))
                        return Plugin;
                }
                catch
                {
                    // A malformed image must not make auto-detection throw; try the next plugin.
                }
            }

            return null;
        }

        public static string GetFilesystemType(Types.FsType Type)
        {
            switch (Type)
            {
                case Types.FsType.Cpio: return "CPIO (ASCII)";
                case Types.FsType.Ext2: return "Ext2";
                case Types.FsType.Ext3: return "Ext3";
                case Types.FsType.Ext4: return "Ext4";
                case Types.FsType.SquashFs: return "SquashFs";
                case Types.FsType.CramFs: return "CramFs";
                case Types.FsType.RomFs: return "RomFs";
                case Types.FsType.Tar: return "Tar";
                case Types.FsType.Fat: return "Fat";
                case Types.FsType.F2fs: return "F2FS";
                case Types.FsType.Iso9660: return "ISO9660";
                case Types.FsType.ExFat: return "exFAT";
                case Types.FsType.EroFs: return "EROFS";
                case Types.FsType.Udf: return "UDF";
                default: return "Unknown";
            }
        }
    }
}
