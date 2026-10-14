using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    /// <summary>
    /// u-boot legacy image (uImage): a single-payload container.
    /// The payload type is declared in the header (kernel / ramdisk / flat_dt), so it is exposed
    /// as one contained element and can be loaded individually by its declared kind.
    /// </summary>
    class LegacyImagePlugin : Base.CompositeImagePlugin
    {
        public LegacyImagePlugin() : base("image.legacy", "legacy") { }

        public override bool DetectImage(ImageData Image)
        {
            if ((Image.Length < 0x40) || (Image.ReadUInt32BE(0) != 0x27051956u))
                return false;

            var Type = (ImageType)Image.ReadByte(0x1E);
            return (Type == ImageType.IH_TYPE_KERNEL) ||
                   (Type == ImageType.IH_TYPE_RAMDISK) ||
                   (Type == ImageType.IH_TYPE_FLATDT);
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            var Img = new LegacyImage(Image.ReadArray(0, Image.Length));
            var Payload = Img.ImageData;
            if (Payload == null)
                return new ContainedImage[0];

            string Name;
            ImageKind Kind;
            switch (Img.Type)
            {
                case ImageType.IH_TYPE_KERNEL: Name = "kernel"; Kind = ImageKind.Kernel; break;
                case ImageType.IH_TYPE_RAMDISK: Name = "ramdisk"; Kind = ImageKind.Filesystem; break;
                case ImageType.IH_TYPE_FLATDT: Name = "devtree"; Kind = ImageKind.Devtree; break;
                default: Name = "payload"; Kind = ImageKind.Unknown; break;
            }

            return new ContainedImage[]
            {
                new ContainedImage(Name, Kind, "legacy", -1, Payload.Length, Payload, Img.Compression)
            };
        }

        public override Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data)
        {
            var Img = new LegacyImage(Data);
            return (Img.Type == ImageType.IH_TYPE_KERNEL) ? new Elements.Kernel.Reader.LegacyReader(Data) : null;
        }

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data)
        {
            var Img = new LegacyImage(Data);
            return (Img.Type == ImageType.IH_TYPE_RAMDISK) ? new Elements.Fs.Reader.LegacyReader(Data) : null;
        }
    }
}
