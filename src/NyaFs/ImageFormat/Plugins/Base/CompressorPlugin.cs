using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.Base
{
    public class CompressorPlugin : NyaPlugin
    {
        public readonly string CompressorName;

        /// <summary>
        /// Compression type this plugin implements (uboot CompressionType id)
        /// </summary>
        public readonly CompressionType Compression;

        private bool Write;
        private bool Read;

        public CompressorPlugin(string Name, string CompressorName, CompressionType Compression, bool Read, bool Write) : base(Name, "compressor")
        {
            this.CompressorName = CompressorName;
            this.Compression = Compression;

            this.Read = Read;
            this.Write = Write;
        }

        /// <summary>
        /// Is read supported
        /// </summary>
        public virtual bool SupportRead => Read;

        /// <summary>
        /// Is write supported
        /// </summary>
        public virtual bool SupportWrite => Write;

        /// <summary>
        /// Detect whether the data starts with this compression stream (magic/header).
        /// </summary>
        public virtual bool DetectCompressor(ImageData Data) => false;

        /// <summary>
        /// Decompress
        /// </summary>
        /// <returns></returns>
        public virtual byte[] Decompress(byte[] Data) => null;

        /// <summary>
        /// Compress
        /// </summary>
        /// <returns></returns>
        public virtual byte[] Compress(byte[] Data) => null;
    }
}
