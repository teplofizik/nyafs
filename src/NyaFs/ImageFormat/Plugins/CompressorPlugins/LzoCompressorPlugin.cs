using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.CompressorPlugins
{
    class LzoCompressorPlugin : Base.CompressorPlugin
    {
        // lzop magic: 89 4C 5A 4F 00 0D 0A 1A 0A (compression into lzop container not implemented yet)
        public LzoCompressorPlugin() : base("compr.lzo", "lzo", CompressionType.IH_COMP_LZO, true, false) { }

        public override bool DetectCompressor(ImageData Data) =>
            (Data.Length >= 4) && (Data.ReadUInt32(0) == 0x4f5a4c89u);

        public override byte[] Decompress(byte[] Data) => Compressors.LZO.Decompress(Data);
    }
}
