namespace NyaFs.ImageFormat.Plugins.ImagePlugins.KernelPlugins
{
    /// <summary>
    /// Raw kernel blob: ELF or COFF (PE) executable.
    /// </summary>
    class RawKernelPlugin : Base.KernelPlugin
    {
        public RawKernelPlugin() : base("kernel.raw", "raw", true, false, true) { }

        public override bool DetectKernel(ImageData Image)
        {
            if (Image.Length < 4) return false;

            if (Image.ReadUInt32BE(0) == 0x7f454c46u) // \x7FELF
                return true;

            if (Image.ReadUInt16(0) == 0x5a4du) // "MZ" (COFF/PE)
                return true;

            return false;
        }

        public override Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data) =>
            new Elements.Kernel.Reader.ArchiveReader(Data);
    }
}
