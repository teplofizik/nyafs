using System;
using System.IO;
using NyaFs.ImageFormat.Elements.Dtb;
using NyaFs.ImageFormat.Elements.Fs;
using NyaFs.ImageFormat.Elements.Kernel;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Turns declarative content of plan nodes into real elements (kernel/filesystem/
    /// devicetree objects or raw bytes) so nested containers (FIT/Android) can be built
    /// from files and directories without intermediate files.
    /// </summary>
    public static class PlanMaterializer
    {
        public static bool Apply(ImageBuildNode Node, PluginRegistry Plugins,
            string BaseDir, string BlobDir, out string Error)
        {
            Error = null;

            foreach (var Child in Node.Children)
                if (!Apply(Child, Plugins, BaseDir, BlobDir, out Error))
                    return false;

            if ((Node.Content == null) || Node.Dirty)
                return true;

            var C = Node.Content;
            if (C.Empty)
                return true;

            if (!String.IsNullOrEmpty(C.Dir))
            {
                var Directory = PlanContentResolver.ResolvePath(C.Dir, BaseDir);
                if (!System.IO.Directory.Exists(Directory))
                {
                    Error = $"Content directory '{Directory}' for '{Node.Path}' is not found";
                    return false;
                }

                var Format = !String.IsNullOrEmpty(Node.Format) ? Node.Format
                    : (C.Options.TryGetValue("fs", out var FsOption) ? FsOption : null);
                if (String.IsNullOrEmpty(Format))
                {
                    Error = $"Content directory for '{Node.Path}' needs a filesystem format";
                    return false;
                }

                if (Node.Kind == ImageKind.Filesystem)
                {
                    Node.Fs = PlanContentResolver.BuildFilesystemObject(Format, Directory, C, out Error);
                    if (Node.Fs != null)
                        Node.Raw = null;
                    return Node.Fs != null;
                }

                var Fs = PlanContentResolver.BuildFilesystemObject(Format, Directory, C, out Error);
                if (Fs == null)
                    return false;

                Node.Raw = PlanContentResolver.SerializeFilesystem(Fs, Format, Plugins, out Error);
                return Node.Raw != null;
            }

            if (!PlanContentResolver.TryResolve(Node, Plugins, BaseDir, BlobDir,
                out var Data, out var Skip, out var ResolveError))
                return true;

            if (!String.IsNullOrEmpty(ResolveError))
            {
                Error = ResolveError;
                return false;
            }

            if (Skip)
                return true;

            switch (Node.Kind)
            {
                case ImageKind.Kernel:
                    if (!LoadElement(Data, ImageKind.Kernel, String.IsNullOrEmpty(Node.Format) ? "raw" : Node.Format,
                        Node, Plugins, out Error))
                        return false;
                    Node.Raw = Data;
                    return true;

                case ImageKind.Devtree:
                    if (!LoadElement(Data, ImageKind.Devtree, String.IsNullOrEmpty(Node.Format) ? "dtb" : Node.Format,
                        Node, Plugins, out Error))
                        return false;
                    Node.Raw = Data;
                    return true;

                case ImageKind.Filesystem:
                    if (!LoadElement(Data, ImageKind.Filesystem, Node.Format, Node, Plugins, out Error))
                        return false;
                    Node.Raw = Data;
                    return true;

                default:
                    Node.Raw = Data;
                    return true;
            }
        }

        private static bool LoadElement(byte[] Data, ImageKind Kind, string Format,
            ImageBuildNode Node, PluginRegistry Plugins, out string Error)
        {
            Error = null;

            var Blob = new BaseImageBlob();
            var Pipeline = new ImagePipeline(Plugins, Blob);
            if (!Pipeline.LoadInto(Data, Kind, Format, Node.Info))
            {
                Error = $"Cannot load {Kind}/{Format ?? "?"} content of '{Node.Path}'";
                return false;
            }

            switch (Kind)
            {
                case ImageKind.Kernel:
                    Node.Kernel = Blob.GetKernel(0);
                    return Node.Kernel != null;
                case ImageKind.Devtree:
                    Node.Dtb = Blob.GetDevTree(0);
                    return Node.Dtb != null;
                case ImageKind.Filesystem:
                    Node.Fs = Blob.GetFilesystem(0);
                    return Node.Fs != null;
                default:
                    return true;
            }
        }
    }
}
