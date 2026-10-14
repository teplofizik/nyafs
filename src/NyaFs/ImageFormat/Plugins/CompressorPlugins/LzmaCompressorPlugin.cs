using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.CompressorPlugins
{
    class LzmaCompressorPlugin : Base.CompressorPlugin
    {
        public LzmaCompressorPlugin() : base("compr.lzma", "lzma", CompressionType.IH_COMP_LZMA, true, true) { }

        public override bool DetectCompressor(ImageData Data) =>
            (Data.Length >= 2) && (Data.ReadUInt16(0) == 0x005d);

        public override byte[] Decompress(byte[] Data) => Compressors.Lzma.Decompress(Data);

        public override byte[] Compress(byte[] Data) => Compressors.Lzma.CompressWithHeader(Data);
    }
}
