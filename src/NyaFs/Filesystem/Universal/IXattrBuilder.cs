namespace NyaFs.Filesystem.Universal
{
    /// <summary>
    /// Optional capability of a filesystem builder: attach extended attributes to an item
    /// identified by its path. Builders that do not support xattrs simply do not implement it.
    /// </summary>
    public interface IXattrBuilder
    {
        void Xattr(string Path, string Name, byte[] Value);
    }
}
