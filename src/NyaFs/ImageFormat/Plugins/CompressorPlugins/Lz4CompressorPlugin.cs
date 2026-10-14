using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.CompressorPlugins
{
    class Lz4CompressorPlugin : Base.CompressorPlugin
    {
        public Lz4CompressorPlugin() : base("compr.lz4", "lz4", CompressionType.IH_COMP_LZ4, true, true) { }

        public override bool DetectCompressor(ImageData Data) =>
            (Data.Length >= 4) && (Data.ReadUInt32(0) == 0x184d2204u);

        public override byte[] Decompress(byte[] Data) => Compressors.Lz4.Decompress(Data);

        public override byte[] Compress(byte[] Data) => Compressors.Lz4.CompressWithHeader(Data);
    }
}
