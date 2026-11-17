namespace NyaFs.ImageFormat
{
    /// <summary>
    /// Result of image detection: what kind of element and in which format it is stored.
    /// </summary>
    public class DetectedImage
    {
        public readonly ImageKind Kind;
        public readonly string Format;

        public DetectedImage(ImageKind Kind, string Format)
        {
            this.Kind = Kind;
            this.Format = Format;
        }

        public bool IsKnown => Kind != ImageKind.Unknown;

        public static DetectedImage Unknown => new DetectedImage(ImageKind.Unknown, "unknown");

        public override string ToString() => $"{Kind}/{Format}";
    }
}
