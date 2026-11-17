using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.CompressorPlugins
{
    class Bz2CompressorPlugin : Base.CompressorPlugin
    {
        public Bz2CompressorPlugin() : base("compr.bz2", "bz2", CompressionType.IH_COMP_BZIP2, true, true) { }

        public override bool DetectCompressor(ImageData Data) =>
            (Data.Length >= 4) &&
            (Data.ReadByte(0) == 0x42) && (Data.ReadByte(1) == 0x5A) && (Data.ReadByte(2) == 0x68);

        public override byte[] Decompress(byte[] Data) => Compressors.Bzip2.Decompress(Data);

        public override byte[] Compress(byte[] Data) => Compressors.Bzip2.CompressWithHeader(Data);
    }
}
