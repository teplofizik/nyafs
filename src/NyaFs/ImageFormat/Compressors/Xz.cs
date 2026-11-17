using System;

namespace NyaFs.ImageFormat.Compressors
{
    public static class Xz
    {
        public static byte[] CompressWithHeader(byte[] Data) => NyaXZ.XzCompressor.Compress(Data);

        public static byte[] Decompress(byte[] Data) => NyaXZ.XzDecompressor.Decompress(Data);
    }
}
