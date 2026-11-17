using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat
{
    /// <summary>
    /// A sub-image located by <see cref="ImageInspector.Locate"/>: its content plus the
    /// kind/format declared by the container (used to force-load when content detection fails).
    /// </summary>
    public class LocatedImage
    {
        public readonly byte[] Content;
        public readonly ImageKind Kind;
        public readonly string Format;
        public readonly CompressionType Compression;
        public readonly ImageInfo Info;

        public LocatedImage(byte[] Content, ImageKind Kind, string Format, CompressionType Compression, ImageInfo Info = null)
        {
            this.Content = Content;
            this.Kind = Kind;
            this.Format = Format;
            this.Compression = Compression;
            this.Info = Info;
        }
    }
}
