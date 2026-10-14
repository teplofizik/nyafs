using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.CompressorPlugins
{
    class ZStdCompressorPlugin : Base.CompressorPlugin
    {
        public ZStdCompressorPlugin() : base("compr.zstd", "zstd", CompressionType.IH_COMP_ZSTD, true, true) { }

        public override bool DetectCompressor(ImageData Data) =>
            (Data.Length >= 4) && (Data.ReadUInt32(0) == 0xfd2fb528u);

        public override byte[] Decompress(byte[] Data) => Compressors.ZStd.Decompress(Data);

        public override byte[] Compress(byte[] Data) => Compressors.ZStd.CompressWithHeader(Data);
    }
}
