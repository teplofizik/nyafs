using System.Collections.Generic;

namespace NyaFs.Filesystem.Universal
{
    /// <summary>
    /// Optional capability for <see cref="IFilesystemReader"/> implementations that can report
    /// the root directory's extended attributes.
    /// </summary>
    public interface IRootXattrs
    {
        /// <summary>
        /// Get root directory xattrs (full name -> raw value), or null when there are none.
        /// </summary>
        Dictionary<string, byte[]> GetRootXattrs();
    }
}
