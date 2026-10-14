namespace NyaFs.ImageFormat.Plugins.ImagePlugins.KernelPlugins
{
    /// <summary>
    /// Linux ARM zImage (self-extracting, may embed a gzip/xz payload).
    /// </summary>
    class ZImageKernelPlugin : Base.KernelPlugin
    {
        public ZImageKernelPlugin() : base("kernel.zimage", "zimage", true, false, true) { }

        public override bool DetectKernel(ImageData Image)
        {
            if (Image.Length < 0x28) return false;

            var Magic = Image.ReadUInt32BE(0x24);
            return (Magic == 0x18286F01u) || (Magic == 0x016F2818u);
        }

        public override Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data) =>
            new Elements.Kernel.Reader.zImageReader(Data);
    }
}
