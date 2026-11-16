using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Elements.Kernel.Reader
{
    /// <summary>
    /// Bare Linux arm64/riscv Image. The payload is kept as-is (including its own
    /// 64-byte header), so it can be booted by U-Boot `booti` after an edit.
    /// </summary>
    public class ImageKernelReader : Reader
    {
        private readonly byte[] Raw;

        public ImageKernelReader(byte[] Raw)
        {
            this.Raw = Raw;
        }

        public override void ReadToKernel(LinuxKernel Dst)
        {
            var Header = new LinuxImageHeader(Raw);
            if (!Header.IsValid)
            {
                Log.Error(0, "Invalid Linux Image header.");
                return;
            }

            Dst.Image = Raw;
            Dst.Info.OperatingSystem = OS.IH_OS_LINUX;
            Dst.Info.Architecture = Header.Architecture;
            Dst.Info.Type = ImageType.IH_TYPE_KERNEL;
            Dst.Info.Compression = CompressionType.IH_COMP_NONE;
            Dst.Info.Name = Header.IsArm64 ? "Linux arm64 Image" : "Linux riscv Image";
        }
    }
}
