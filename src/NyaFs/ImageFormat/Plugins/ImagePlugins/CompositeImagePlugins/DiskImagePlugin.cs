using NyaFs.ImageFormat.Composite;
using NyaFs.ImageFormat.Plugins.Base;
using System.Collections.Generic;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    /// <summary>
    /// Generic raw MBR/GPT disk image. Partitions are returned as Unknown so the
    /// normal pipeline detects the filesystem/kernel inside each one.
    /// </summary>
    public class DiskImagePlugin : CompositeImagePlugin
    {
        public DiskImagePlugin() : base("image.disk", "disk")
        {
        }

        public override bool SupportWrite => true;

        public override bool DetectImage(ImageData Image)
        {
            if (Image.Length < DiskImage.SectorSize)
                return false;
            return DiskImage.TryParse(Image.ReadArray(0, Image.Length), out _);
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            var Raw = Image.ReadArray(0, Image.Length);
            if (!DiskImage.TryParse(Raw, out var Disk))
                return new ContainedImage[0];

            var Result = new List<ContainedImage>();
            foreach (var Partition in Disk.Partitions)
            {
                var Content = Partition.Read(Raw);
                if (Content == null)
                    continue;
                Result.Add(new ContainedImage(Partition.Name, ImageKind.Unknown, null,
                    Partition.Offset, Partition.Size, Content));
            }
            return Result.ToArray();
        }

        public override string ReadDescription(ImageData Image) =>
            DiskImage.TryParse(Image.ReadArray(0, Image.Length), out var Disk)
                ? (Disk.IsGpt ? "GPT disk image" : "MBR disk image")
                : null;

        public override byte[] Build(BaseImageBlob Blob, out string Error) =>
            DiskImageWriter.Build(Blob, out Error);
    }
}