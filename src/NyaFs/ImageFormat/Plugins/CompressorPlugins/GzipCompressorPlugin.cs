using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.CompressorPlugins
{
    class GzipCompressorPlugin : Base.CompressorPlugin
    {
        public GzipCompressorPlugin() : base("compr.gzip", "gzip", CompressionType.IH_COMP_GZIP, true, true) { }

        public override bool DetectCompressor(ImageData Data) =>
            (Data.Length >= 2) && (Data.ReadUInt16(0) == 0x8b1f);

        public override byte[] Decompress(byte[] Data) => Compressors.Gzip.Decompress(Data);

        public override byte[] Compress(byte[] Data) => Compressors.Gzip.CompressWithHeader(Data);
    }
}
