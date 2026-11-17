using System;
using System.Collections.Generic;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// `content.compression` handling: compress the resolved bytes (file, blob or
    /// filesystem built from a directory) with a registered compressor plugin.
    /// Compression is applied on output; supply pre-compressed files without it.
    /// </summary>
    public static class PlanCompression
    {
        public static bool TryApply(byte[] Data, string Compression, PluginRegistry Plugins,
            out byte[] Result, out string Error)
        {
            Result = Data;
            Error = null;

            if (String.IsNullOrWhiteSpace(Compression) ||
                String.Equals(Compression.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                return true;

            if (Data == null)
            {
                Error = "No content to compress";
                return false;
            }

            var Plugin = Resolve(Plugins, Compression.Trim());
            if (Plugin == null)
            {
                Error = $"Unknown compression '{Compression}'";
                return false;
            }

            if (!Plugin.SupportWrite)
            {
                Error = $"Compression '{Compression}' is read-only";
                return false;
            }

            try
            {
                Result = Plugin.Compress(Data);
            }
            catch (Exception E)
            {
                Error = $"Cannot compress content with '{Compression}': {E.Message}";
                return false;
            }

            if (Result == null)
            {
                Error = $"Compressor '{Compression}' produced no data";
                return false;
            }

            return true;
        }

        public static string NameOf(Types.CompressionType Compression)
        {
            switch (Compression)
            {
                case Types.CompressionType.IH_COMP_GZIP: return "gzip";
                case Types.CompressionType.IH_COMP_LZMA: return "lzma";
                case Types.CompressionType.IH_COMP_LZ4: return "lz4";
                case Types.CompressionType.IH_COMP_BZIP2: return "bzip2";
                case Types.CompressionType.IH_COMP_ZSTD: return "zstd";
                default: return "none";
            }
        }

        private static NyaFs.ImageFormat.Plugins.Base.CompressorPlugin Resolve(
            PluginRegistry Plugins, string Name)
        {
            if (Plugins == null)
                return null;

            foreach (var Plugin in Plugins.GetCompressors())
            {
                if (String.Equals(Plugin.Name, Name, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(Plugin.Name, "compr." + Name, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(Plugin.CompressorName, Name, StringComparison.OrdinalIgnoreCase))
                    return Plugin;
            }

            return null;
        }
    }
}
