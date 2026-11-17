namespace NyaFs.ImageFormat
{
    /// <summary>
    /// Kind of an image element that the pipeline can detect/load.
    /// </summary>
    public enum ImageKind
    {
        Unknown = 0,
        Kernel,
        Filesystem,
        Devtree
    }
}
