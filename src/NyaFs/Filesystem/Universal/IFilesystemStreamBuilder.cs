using System.IO;

namespace NyaFs.Filesystem.Universal
{
    /// <summary>
    /// Filesystem builders that can emit their image directly to a stream without
    /// allocating the whole result in a byte[].
    /// </summary>
    public interface IFilesystemStreamBuilder
    {
        void WriteFilesystemImage(Stream Output);
    }
}
