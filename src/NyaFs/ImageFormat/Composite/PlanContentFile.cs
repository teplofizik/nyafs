using System;
using System.IO;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Fast path for layout payloads that are a single uncompressed file: writers
    /// can copy the file to the output stream instead of loading it into a byte[].
    /// </summary>
    public static class PlanContentFile
    {
        public static bool TryGet(ImageBuildNode Node, string BaseDir,
            out string Path, out long Length, out bool Skip, out string Error)
        {
            Path = null;
            Length = 0;
            Skip = false;
            Error = null;

            if ((Node == null) || Node.Dirty || (Node.Content == null))
                return false;

            var Content = Node.Content;

            if (Content.Empty)
            {
                Skip = true;
                return true;
            }

            if (String.IsNullOrEmpty(Content.File))
                return false;

            // Compressed content needs the in-memory serializer.
            if (!String.IsNullOrWhiteSpace(Content.Compression))
                return false;

            Path = PlanContentResolver.ResolvePath(Content.File, BaseDir);
            if (!File.Exists(Path))
            {
                Error = $"Content file '{Path}' for '{Node.Path}' is not found";
                return true;
            }

            Length = new FileInfo(Path).Length;
            return true;
        }
    }
}
