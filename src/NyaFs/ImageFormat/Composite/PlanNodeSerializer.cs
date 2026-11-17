using System;
using NyaFs.ImageFormat.Plugins;

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
