namespace NyaFs.Filesystem.Universal
{
    /// <summary>
    /// Optional capability for <see cref="IFilesystemReader"/> implementations that can report
    /// the root directory's owner and permission bits (so a loaded filesystem keeps its root
    /// mode/uid/gid on round-trip instead of defaulting to 0755:0:0).
    /// </summary>
    public interface IRootFsInfo
    {
        /// <summary>
        /// Get root directory owner/permissions. Mode is the 12-bit permission part (no S_IFDIR).
        /// Returns false if the reader cannot provide this information.
        /// </summary>
        bool TryGetRootFsItem(out uint User, out uint Group, out uint Mode);
    }
}
