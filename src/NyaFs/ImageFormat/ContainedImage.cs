using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat
{
    /// <summary>
    /// An element found inside a composite container.
    /// <see cref="Kind"/>/<see cref="Format"/>/<see cref="Compression"/> are the *declared*
    /// type from the container metadata (may be <see cref="ImageKind.Unknown"/> when the
    /// container does not state the type). <see cref="Content"/> is the raw payload so the
    /// loader can either force-load it by declared type or re-run detection on it.
    /// </summary>
    public class ContainedImage
    {
        public readonly string Name;
        public readonly ImageKind Kind;
        public readonly string Format;
        public readonly CompressionType Compression;
        public readonly long Offset;
        public readonly long Size;
        public readonly byte[] Content;

        public ContainedImage(string Name, ImageKind Kind, string Format, long Offset, long Size, byte[] Content, CompressionType Compression = CompressionType.IH_COMP_NONE)
        {
            this.Name = Name;
            this.Kind = Kind;
            this.Format = Format;
            this.Offset = Offset;
            this.Size = Size;
            this.Content = Content;
            this.Compression = Compression;
        }

        public override string ToString() => $"{Name}: {Kind}/{Format ?? "?"} ({Size} bytes)";
    }
}
