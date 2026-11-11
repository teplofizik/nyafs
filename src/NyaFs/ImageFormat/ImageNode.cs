using System.Collections.Generic;

namespace NyaFs.ImageFormat
{
    /// <summary>One configuration entry from a FIT/container header.</summary>
    public class ImageContainerConfiguration
    {
        public string Name = "";
        public string Description = "";
        public string Kernel;
        public string Ramdisk;
        public string Fdt;
    }

    /// <summary>
    /// A node of the image hierarchy discovered by <see cref="ImageInspector"/>.
    /// <see cref="Path"/> is a stable identifier (identical for listing and for loading
    /// a specific sub-image), relative to the root image.
    /// </summary>
    public class ImageNode
    {
        /// <summary>Stable path of this element (e.g. "config@cv1835/kernel"). Root path is "".</summary>
        public string Path = "";

        /// <summary>Short name (last path segment).</summary>
        public string Name = "";

        /// <summary>Container format this element was found in (fit/android/bmu/...), null for root.</summary>
        public string Container = null;

        /// <summary>Detected or declared kind.</summary>
        public ImageKind Kind = ImageKind.Unknown;

        /// <summary>Detected or declared format (squashfs/cpio/raw/...), may be null.</summary>
        public string Format = null;

        /// <summary>Compression applied to this element (packaging), IH_COMP_NONE if none.</summary>
        public Types.CompressionType Compression = Types.CompressionType.IH_COMP_NONE;

        /// <summary>Size of this element's payload in bytes.</summary>
        public long Size = 0;

        /// <summary>Raw bytes of this element as loaded (before transparent decompression).</summary>
        public byte[] Raw = null;

        /// <summary>Container metadata, when declared by FIT or another container.</summary>
        public Types.ImageInfo Info = null;

        /// <summary>Options read from the container header.</summary>
        public readonly Dictionary<string, string> Options = new Dictionary<string, string>();

        /// <summary>Default configuration name, when the container declares one.</summary>
        public string DefaultConfiguration = null;

        /// <summary>All container configurations, in source order.</summary>
        public readonly List<ImageContainerConfiguration> Configurations = new List<ImageContainerConfiguration>();

        /// <summary>Nested elements (for composite containers).</summary>
        public readonly List<ImageNode> Children = new List<ImageNode>();

        public bool IsContainer => Children.Count > 0;

        public override string ToString() => $"{Path} [{Kind}/{Format ?? "?"}] {Size} bytes, comp {Compression}";
    }
}
