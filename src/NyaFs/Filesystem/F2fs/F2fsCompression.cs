namespace NyaFs.Filesystem.F2fs
{
    /// <summary>
    /// F2FS per-file compression algorithm (F2FS_FEATURE_COMPRESSION).
    /// Values match the on-disk i_compress_algorithm field.
    /// </summary>
    public enum F2fsCompression
    {
        None = -1,
        Lzo = 0,
        Lz4 = 1,
        Zstd = 2,
        LzoRle = 3
    }
}
