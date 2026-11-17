using System;
using System.IO;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Helpers for template-based vendor layouts: the sample image carries the
    /// vendor header/metadata, while the schema lists only entries to replace or add.
    /// </summary>
    public static class PlanTemplate
    {
        public static byte[] Load(PlanDocument Document, string Format, out string Error)
        {
            Error = null;

            if (!Document.Options.TryGetValue("template", out var Template) || String.IsNullOrWhiteSpace(Template))
            {
                Error = $"{Format} layout needs options.template (a sample image)";
                return null;
            }

            Document.Options.TryGetValue("plan.base", out var BaseDir);
            var Path = PlanContentResolver.ResolvePath(Template.Trim(), BaseDir);
            if (!File.Exists(Path))
            {
                Error = $"Template not found: {Path}";
                return null;
            }

            return File.ReadAllBytes(Path);
        }

        public static bool HasProvidedContent(ImageBuildNode Node)
        {
            if (Node == null)
                return false;

            foreach (var Child in Node.Children)
            {
                if ((Child.Content != null) || (Child.Fs != null) || (Child.Kernel != null) ||
                    (Child.Dtb != null) || ((Child.Raw != null) && (Child.Raw.Length > 0)))
                    return true;

                if (HasProvidedContent(Child))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Serializes every provided child into Node.Raw and marks it dirty so the
        /// existing source-based writer replaces/append it.
        /// </summary>
        public static bool ResolveProvided(ImageBuildNode Root, PluginRegistry Plugins, out string Error)
        {
            Error = null;
            Root.Options.TryGetValue("plan.base", out var BaseDir);
            Root.Options.TryGetValue("plan.blobs", out var BlobDir);
            return ResolveNode(Root, Plugins, BaseDir, BlobDir, out Error);
        }

        private static bool ResolveNode(ImageBuildNode Node, PluginRegistry Plugins,
            string BaseDir, string BlobDir, out string Error)
        {
            Error = null;

            foreach (var Child in Node.Children)
            {
                var Provided = (Child.Content != null) || (Child.Fs != null) ||
                    (Child.Kernel != null) || (Child.Dtb != null) ||
                    ((Child.Raw != null) && (Child.Raw.Length > 0));

                if (Provided)
                {
                    if (!PlanNodeSerializer.TrySerialize(Child, Plugins, BaseDir, BlobDir,
                        out var Data, out var Skip, out var Err))
                    {
                        Error = Err;
                        return false;
                    }

                    if (!Skip && (Data != null))
                    {
                        Child.Raw = Data;
                        Child.Dirty = true;
                    }
                    continue;
                }

                if (!ResolveNode(Child, Plugins, BaseDir, BlobDir, out Error))
                    return false;
            }

            return true;
        }
    }
}
