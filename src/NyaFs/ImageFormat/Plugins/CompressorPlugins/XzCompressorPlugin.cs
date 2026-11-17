using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.CompressorPlugins
{
    class XzCompressorPlugin : Base.CompressorPlugin
    {
        // xz magic: FD 37 7A 58 5A 00
        public XzCompressorPlugin() : base("compr.xz", "xz", CompressionType.IH_COMP_XZ, true, true) { }

        public override bool DetectCompressor(ImageData Data) =>
            (Data.Length >= 6) && (Data.ReadUInt32BE(0) == 0xfd377a58u);

        public override byte[] Decompress(byte[] Data) => Compressors.Xz.Decompress(Data);

        public override byte[] Compress(byte[] Data) => Compressors.Xz.CompressWithHeader(Data);
    }
}
