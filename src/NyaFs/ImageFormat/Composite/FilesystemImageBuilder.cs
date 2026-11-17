using System;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Builds a standalone filesystem image from a layout whose root format is a
    /// filesystem (squashfs, ext4, fat, ...). The content is the root `content.dir`;
    /// filesystem parameters live in root `options`.
    /// </summary>
    public static class FilesystemImageBuilder
    {
        public static byte[] Build(PlanDocument Document, PluginRegistry Plugins, out string Error)
        {
            Error = null;

            if ((Document == null) || String.IsNullOrEmpty(Document.Format))
            {
                Error = "Layout has no filesystem format";
                return null;
            }

            var Plugin = Plugins?.ResolveFilesystem(Document.Format);
            if (Plugin == null)
            {
                Error = $"Unknown filesystem format '{Document.Format}'";
                return null;
            }

            if (!Plugin.SupportWrite)
            {
                Error = $"Filesystem '{Document.Format}' is read-only";
                return null;
            }

            var Content = Document.Content;
            if ((Content == null) || String.IsNullOrEmpty(Content.Dir))
            {
                Error = $"Filesystem layout '{Document.Format}' needs content: {{ dir: ... }}";
                return null;
            }

            Document.Options.TryGetValue("plan.base", out var BaseDir);
            var DirPath = PlanContentResolver.ResolvePath(Content.Dir, BaseDir);
            if (!System.IO.Directory.Exists(DirPath))
            {
                Error = $"Content directory '{DirPath}' is not found";
                return null;
            }

            var Fs = PlanContentResolver.BuildFilesystemObject(Document.Format, DirPath, Content, out Error);
            if (Fs == null)
                return null;

            if (!FilesystemPlanOptions.Apply(Fs, Document.Format, Document.Options, Plugins, out Error))
                return null;

            return PlanContentResolver.SerializeFilesystem(Fs, Document.Format, Plugins, out Error);
        }
    }
}
