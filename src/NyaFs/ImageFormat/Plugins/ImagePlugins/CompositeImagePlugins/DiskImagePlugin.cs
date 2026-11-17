using System.Collections.Generic;
using NyaFs.ImageFormat.Composite;
using NyaFs.ImageFormat.Plugins;
using NyaFs.ImageFormat.Plugins.Base;

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

                // Truncated SD images may end before the declared partition slot.
                // Keep the slot visible but do not feed partial filesystem data.
                var Truncated = (Partition.Offset + Partition.Size) > Raw.Length;
                Result.Add(new ContainedImage(Partition.Name, ImageKind.Unknown,
                    Truncated ? "truncated" : null,
                    Partition.Offset, Partition.Size, Truncated ? null : Content));
            }
            return Result.ToArray();
        }

        public override string ReadDescription(ImageData Image) =>
            DiskImage.TryParse(Image.ReadArray(0, Image.Length), out var Disk)
                ? (Disk.IsGpt ? "GPT disk image" : "MBR disk image")
                : null;

        public override bool SupportLayout => true;

        public override ImageBuildNode CreatePlan(PlanDocument Document, out string Error)
        {
            Error = null;

            var Table = Document.Options.TryGetValue("table", out var TableValue) && !string.IsNullOrWhiteSpace(TableValue)
                ? TableValue.Trim().ToLowerInvariant()
                : "gpt";

            if ((Table != "mbr") && (Table != "gpt"))
            {
                Error = "Disk layout 'table' must be 'mbr' or 'gpt'";
                return null;
            }

            if ((Table == "mbr") && (Document.Children.Count > 4))
            {
                Error = "MBR supports at most 4 partitions; use 'table: gpt'";
                return null;
            }

            var Root = new ImageBuildNode
            {
                Name = "disk",
                Path = "",
                Kind = ImageKind.Unknown,
                Format = Format
            };

            Root.Options["table"] = Table;
            foreach (var Key in new[] { "total", "align", "seed" })
                if (Document.Options.TryGetValue(Key, out var Value) && !string.IsNullOrWhiteSpace(Value))
                    Root.Options[Key] = Value.Trim();

            foreach (var Child in Document.Children)
                Root.Children.Add(PlanNodes.FromElement(Child, ""));

            return Root;
        }

        public override byte[] Build(BaseImageBlob Blob, out string Error) =>
            DiskImageWriter.Build(Blob, out Error);

        public override byte[] Build(BaseImageBlob Blob, PluginRegistry Plugins, out string Error)
        {
            var Root = Blob?.BuildPlan?.Root;
            if ((Root != null) && (Root.Raw == null))
                return DiskImageBuilder.Build(Root, Plugins, out Error);

            return Build(Blob, out Error);
        }
    }
}