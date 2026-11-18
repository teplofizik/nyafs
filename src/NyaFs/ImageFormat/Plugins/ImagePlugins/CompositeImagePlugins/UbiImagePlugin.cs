using System;
using System.Collections.Generic;
using NyaFs.Filesystem.UbiFs;
using NyaFs.ImageFormat.Composite;
using NyaFs.ImageFormat.Plugins.Base;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    /// <summary>
    /// UBI container with one or more volumes. Volumes are exposed as children so the
    /// normal pipeline can detect UBIFS inside them.
    /// </summary>
    public class UbiImagePlugin : CompositeImagePlugin
    {
        public UbiImagePlugin() : base("image.ubi", "ubi")
        {
        }

        public override bool SupportWrite => true;
        public override bool SupportLayout => true;

        public override bool DetectImage(ImageData Image)
        {
            if (Image.Length < 4)
                return false;
            if (Image.ReadUInt32BE(0) != 0x55424923u) // "UBI#"
                return false;

            // Single-volume UBI keeps the legacy filesystem detection (ubifs).

            try
            {
                var Ubi = new UbiFsReader(Image.ReadArray(0, Image.Length));
                return Ubi.Volumes.Count > 1;
            }
            catch
            {
                return false;
            }
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            var Raw = Image.ReadArray(0, Image.Length);
            UbiFsReader Ubi;
            try { Ubi = new UbiFsReader(Raw); }
            catch { return new ContainedImage[0]; }

            var Result = new List<ContainedImage>();
            foreach (var Volume in Ubi.Volumes)
            {
                if ((Volume.Name == null) || (Volume.LebToPeb.Count == 0))
                    continue;

                var Parts = new List<byte[]>();
                var Total = 0;
                var Lnums = new List<uint>(Volume.LebToPeb.Keys);
                Lnums.Sort();
                foreach (var Lnum in Lnums)
                {
                    var Leb = Ubi.ReadLeb(Volume.Id, Lnum);
                    if (Leb == null)
                        continue;
                    Parts.Add(Leb);
                    Total += Leb.Length;
                }

                var Data = new byte[Total];
                var Offset = 0;
                foreach (var Part in Parts)
                {
                    Array.Copy(Part, 0, Data, Offset, Part.Length);
                    Offset += Part.Length;
                }

                Result.Add(new ContainedImage(Volume.Name, ImageKind.Unknown,
                    null, -1, Data.Length, Data));
            }

            return Result.ToArray();
        }

        public override string ReadDescription(ImageData Image)
        {
            try
            {
                var Ubi = new UbiFsReader(Image.ReadArray(0, Image.Length));
                return $"UBI container ({Ubi.Volumes.Count} volume(s))";
            }
            catch
            {
                return "UBI container";
            }
        }

        public override ImageBuildNode CreatePlan(PlanDocument Document, out string Error)
        {
            Error = null;

            var Root = new ImageBuildNode
            {
                Name = "ubi",
                Path = "",
                Kind = ImageKind.Unknown,
                Format = Format
            };

            foreach (var Key in new[] { "peb", "block", "page", "total" })
                if (Document.Options.TryGetValue(Key, out var Value) && !String.IsNullOrWhiteSpace(Value))
                    Root.Options[Key] = Value.Trim();

            if (Document.Size.HasValue && !Root.Options.ContainsKey("total"))
                Root.Options["total"] = Document.Size.Value.ToString();

            foreach (var Child in Document.Children)
                Root.Children.Add(PlanNodes.FromElement(Child, ""));

            return Root;
        }

        public override byte[] Build(BaseImageBlob Blob, out string Error)
        {
            var Root = Blob?.BuildPlan?.Root;
            if (Root == null)
            {
                Error = "No UBI build plan";
                return null;
            }

            if ((Root.Raw != null) && (Root.Children.Count == 0))
            {
                Error = null;
                return Root.Raw;
            }

            return UbiImageBuilder.Build(Root, PluginHost.Current, out Error);
        }

        public override byte[] Build(BaseImageBlob Blob, PluginRegistry Plugins, out string Error)
        {
            var Root = Blob?.BuildPlan?.Root;
            if (Root == null)
            {
                Error = "No UBI build plan";
                return null;
            }

            if ((Root.Raw != null) && (Root.Children.Count == 0))
            {
                Error = null;
                return Root.Raw;
            }

            return UbiImageBuilder.Build(Root, Plugins, out Error);
        }

        public override bool SupportsStreaming => true;

        public override bool BuildTo(System.IO.Stream Output, BaseImageBlob Blob,
            PluginRegistry Plugins, out string Error)
        {
            var Root = Blob?.BuildPlan?.Root;
            if (Root == null)
            {
                Error = "No UBI build plan";
                return false;
            }

            if ((Root.Raw != null) && (Root.Children.Count == 0))
            {
                Error = null;
                Output.Write(Root.Raw, 0, Root.Raw.Length);
                return true;
            }

            return UbiImageBuilder.BuildTo(Output, Root, Plugins, out Error);
        }
    }
}
