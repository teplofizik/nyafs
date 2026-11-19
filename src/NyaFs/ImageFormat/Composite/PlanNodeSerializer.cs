using System;
using NyaFs.ImageFormat.Plugins;
using NyaFs.ImageFormat.Plugins.Base;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Serializes one plan node to bytes for layout writers: declarative content,
    /// filesystems, raw payloads and nested FIT/Android containers.
    /// </summary>
    public static class PlanNodeSerializer
    {
        public static bool TrySerialize(ImageBuildNode Node, PluginRegistry Plugins, string BaseDir, string BlobDir,
            out byte[] Data, out bool Skip, out string Error)
        {
            Data = null;
            Skip = false;
            Error = null;

            if (PlanContentResolver.TryResolve(Node, Plugins, BaseDir, BlobDir, out var ContentData, out var ContentSkip, out var ContentError))
            {
                if (!string.IsNullOrEmpty(ContentError))
                {
                    Error = ContentError;
                    return false;
                }

                if (ContentSkip)
                {
                    Skip = true;
                    return true;
                }

                Data = ContentData;
                return true;
            }

            if (Node.Children.Count > 0)
            {
                var ContainerPlugin = GetStreamingContainerPlugin(Node, Plugins);
                if (ContainerPlugin != null)
                {
                    Node.Options["plan.base"] = BaseDir ?? "";
                    Node.Options["plan.blobs"] = BlobDir ?? "";

                    var ContainerBlob = new BaseImageBlob { BuildPlan = new ImageBuildPlan(Node) };
                    Data = ContainerPlugin.Build(ContainerBlob, Plugins, out Error);
                    return Data != null;
                }

                if (!PlanMaterializer.Apply(Node, Plugins, BaseDir, BlobDir, out var MaterializeError))
                {
                    Error = MaterializeError;
                    return false;
                }

                if ((Node.Format == "fit") || (Node.Format == "cimg"))
                {
                    var Writer = new ImageBuildPlanWriter(new ImageBuildPlan(Node)) { Registry = Plugins };
                    Data = Writer.Build(out Error);
                    return Data != null;
                }

                if (Node.Format == "android")
                {
                    var Temp = new BaseImageBlob { BuildPlan = new ImageBuildPlan(Node) };
                    foreach (var Child in Node.Children)
                    {
                        if (Child.Kernel != null) Temp.SetKernel(0, Child.Kernel);
                        if (Child.Fs != null) Temp.SetFilesystem(0, Child.Fs);
                        if (Child.Dtb != null) Temp.SetDevTree(0, Child.Dtb);
                    }

                    Data = new AndroidImageWriter("") { Registry = Plugins }.Build(Temp, out Error);
                    return Data != null;
                }

                Error = $"Container '{Node.Format}' at '{Node.Path}' is not supported by the layout writer yet";
                return false;
            }

            if (Node.Fs != null)
            {
                var Format = !string.IsNullOrEmpty(Node.Format) ? Node.Format : Node.Fs.FilesystemFormat;
                if (!FilesystemPlanOptions.Apply(Node.Fs, Format, Node.Options, Plugins, out Error))
                    return false;

                Data = PlanContentResolver.SerializeFilesystem(Node.Fs, Format, Plugins, out Error);
                return Data != null;
            }

            if (IsContainerFormat(Node.Format))
            {
                Error = $"Container '{Node.Format}' at '{Node.Path}' has no children; add its content or a nested layout";
                return false;
            }

            if ((Node.Raw != null) && (Node.Raw.Length > 0))
            {
                Data = Node.Raw;
                return true;
            }

            if (Node.Raw != null)
            {
                Skip = true;
                return true;
            }

            Error = $"Slot '{Node.Path}' has no content";
            return false;
        }

        /// <summary>
        /// Resolve a container plugin that can emit its image directly to a stream.
        /// The capability lives in the plugin (SupportsStreaming/BuildTo), not in a
        /// format list here, so new streaming containers work without core changes.
        /// </summary>
        private static CompositeImagePlugin GetStreamingContainerPlugin(ImageBuildNode Node, PluginRegistry Plugins)
        {
            if ((Node == null) || (Node.Children.Count == 0) || String.IsNullOrEmpty(Node.Format))
                return null;

            var Plugin = Plugins?.ResolveImage(Node.Format);
            if ((Plugin == null) || !Plugin.SupportWrite || !Plugin.SupportsStreaming)
                return null;

            return Plugin;
        }

        /// <summary>
        /// Probe the size of a streamable node (filesystem or nested UBI/disk container)
        /// without keeping its image in memory. Returns false when the node is not
        /// streamable; <paramref name="Handled"/> is true when the node was recognised
        /// and <paramref name="Error"/> is set.
        /// </summary>
        public static bool TryProbeStreaming(ImageBuildNode Node, PluginRegistry Plugins,
            string BaseDir, string BlobDir, out long Length, out bool Handled, out string Error)
        {
            Length = 0;
            Handled = false;
            Error = null;

            if (PlanContentResolver.TryBuildStreamingFilesystem(Node, Plugins, BaseDir,
                out var Writer, out var Fs, out Handled, out Error))
            {
                return Probe(Sink => Writer.WriteFsTo(Sink, Fs)
                    ? null : $"Filesystem '{Node.Format}' produced no data", out Length, out Error);
            }

            if (Handled)
                return false;

            var Container = GetStreamingContainerPlugin(Node, Plugins);
            if (Container == null)
                return false;

            Handled = true;
            return Probe(Sink =>
            {
                if (BuildContainerTo(Sink, Node, Plugins, Container, BaseDir, BlobDir, out var ContainerError))
                    return null;

                return ContainerError ?? $"Container '{Node.Format}' produced no data";
            }, out Length, out Error);
        }

        /// <summary>
        /// Run a writer into a temp file to learn its size. The file keeps the probe
        /// O(1) in RAM while still allowing metadata read-back.
        /// </summary>
        private static bool Probe(Func<System.IO.Stream, string> Write,
            out long Length, out string Error)
        {
            Length = 0;
            Error = null;

            var Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "nya-probe-" + Guid.NewGuid().ToString("N"));

            try
            {
                using (var Sink = new System.IO.FileStream(Path, System.IO.FileMode.CreateNew,
                    System.IO.FileAccess.ReadWrite, System.IO.FileShare.None, 1024 * 1024,
                    System.IO.FileOptions.DeleteOnClose))
                {
                    var WriteError = Write(Sink);
                    if (WriteError != null)
                    {
                        Error = WriteError;
                        return false;
                    }

                    Length = Sink.Length;
                }
            }
            finally
            {
                try { System.IO.File.Delete(Path); } catch { }
            }

            return true;
        }

        /// <summary>Write a streamable node (filesystem or nested UBI/disk container) to the output.</summary>
        public static bool TryWriteStreaming(ImageBuildNode Node, PluginRegistry Plugins,
            string BaseDir, string BlobDir, System.IO.Stream Output, out bool Handled, out string Error)
        {
            Handled = false;
            Error = null;

            if (PlanContentResolver.TryBuildStreamingFilesystem(Node, Plugins, BaseDir,
                out var Writer, out var Fs, out Handled, out Error))
            {
                if (!Writer.WriteFsTo(Output, Fs))
                {
                    Error = $"Filesystem '{Node.Format}' produced no data";
                    Handled = true;
                    return false;
                }

                return true;
            }

            if (Handled)
                return false;

            var Container = GetStreamingContainerPlugin(Node, Plugins);
            if (Container == null)
                return false;

            Handled = true;
            return BuildContainerTo(Output, Node, Plugins, Container, BaseDir, BlobDir, out Error);
        }

        private static bool BuildContainerTo(System.IO.Stream Output, ImageBuildNode Node,
            PluginRegistry Plugins, CompositeImagePlugin Container, string BaseDir, string BlobDir, out string Error)
        {
            Error = null;

            Node.Options["plan.base"] = BaseDir ?? "";
            Node.Options["plan.blobs"] = BlobDir ?? "";

            var ContainerBlob = new BaseImageBlob { BuildPlan = new ImageBuildPlan(Node) };
            return Container.BuildTo(Output, ContainerBlob, Plugins, out Error);
        }

        public static bool IsContainerFormat(string Format)
        {
            if (string.IsNullOrEmpty(Format))
                return false;

            switch (Format.ToLowerInvariant())
            {
                case "fit":
                case "android":
                case "cimg":
                case "ubi":
                case "nand":
                case "disk":
                case "swu":
                case "rkfw":
                    return true;
                default:
                    return false;
            }
        }
    }
}
