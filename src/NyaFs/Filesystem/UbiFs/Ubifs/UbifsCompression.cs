namespace NyaFs.Filesystem.UbiFs.Ubifs
{
    /// <summary>
    /// UBIFS compression types (on-flash %UBIFS_COMPR_* values). Used per data node
    /// (and as the superblock default_compr); the value is stored in the node's
    /// compr_type field.
    /// </summary>
    public enum UbifsCompression : ushort
    {
        None = 0,
        Lzo = 1,
        Zlib = 2,
        Zstd = 3
    }
}
