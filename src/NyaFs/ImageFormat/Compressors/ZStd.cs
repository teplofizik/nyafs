using CrcSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Compressors
{
    static class ZStd
    {
        public static byte[] CompressWithHeader(byte[] Data)
        {
            using var compressor = new ZstdSharp.Compressor();
            return compressor.Wrap(Data).ToArray();
        }

        public static byte[] Decompress(byte[] Data)
        {
            using var decompressor = new ZstdSharp.Decompressor();
            return decompressor.Unwrap(Data).ToArray();
        }
    }
}
