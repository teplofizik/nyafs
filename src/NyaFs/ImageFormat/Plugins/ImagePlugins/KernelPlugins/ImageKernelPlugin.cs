using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.KernelPlugins
{
    /// <summary>
    /// Bare Linux Image (arm64/riscv), as booted by U-Boot `booti`.
    /// </summary>
    class ImageKernelPlugin : Base.KernelPlugin
    {
        public ImageKernelPlugin() : base("kernel.image", "image", true, true, false) { }

        public override bool DetectKernel(ImageData Image)
        {
            if (Image.Length < LinuxImageHeader.HeaderSize)
                return false;

            return new LinuxImageHeader(Image.ReadArray(0, LinuxImageHeader.HeaderSize)).IsValid;
        }

        public override Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data) =>
            new Elements.Kernel.Reader.ImageKernelReader(Data);

        public override Elements.Kernel.Writer.Writer GetKernelWriter(Elements.Kernel.LinuxKernel Kernel) =>
            new Elements.Kernel.Writer.ArchiveWriter(CompressionType.IH_COMP_NONE);
    }
}
