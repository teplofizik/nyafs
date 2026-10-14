using System;
using System.Collections.Generic;
using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat
{
    /// <summary>
    /// Recursively inspects an image and builds a tree of all discoverable sub-images,
    /// each with a stable <see cref="ImageNode.Path"/>.
    ///
    /// Traversal order matches the loading pipeline: compression (transparent, keeps the path)
    /// then composite containers (adds a path segment per contained element), then leaf
    /// kernel / filesystem / devicetree detection. Elements whose type is declared by the
    /// container keep that declared kind even if content detection fails.
    /// </summary>
    public class ImageInspector
    {
        private readonly Processor.Scripting.ScriptPlugins Plugins;
        private const int MaxDepth = 32;

        public ImageInspector(Processor.Scripting.ScriptPlugins Plugins)
        {
            this.Plugins = Plugins;
        }

        public ImageNode Inspect(string Name, byte[] Raw)
        {
            var Root = new ImageNode { Name = Name, Path = "" };
            Build(Root, new ImageData(Raw), Raw.Length, ImageKind.Unknown, null, 0);
            return Root;
        }

        /// <summary>
        /// Locate a sub-image by its stable path (as printed by <see cref="Inspect"/>).
        /// Empty path (or ".") means the whole image.
        /// </summary>
        public LocatedImage Locate(byte[] Raw, string SubPath)
        {
            var Target = (SubPath ?? "").Trim('/');
            if (Target == ".") Target = "";

            return LocateNode(new ImageData(Raw), Target, ImageKind.Unknown, null);
        }

        private LocatedImage LocateNode(ImageData Data, string Target, ImageKind HintKind, string HintFormat)
        {
            if (Target.Length == 0)
                return new LocatedImage(Data.ReadArray(0, Data.Length), HintKind, HintFormat, CompressionType.IH_COMP_NONE);

            // Compression is transparent: unpack and search inside.
            var Compressor = Plugins.DetectCompressor(Data);
            if (Compressor != null)
            {
                byte[] Unpacked = null;
                try { Unpacked = Compressor.Decompress(Data.ReadArray(0, Data.Length)); } catch { }

                if (Unpacked != null)
                {
                    var Res = LocateNode(new ImageData(Unpacked), Target, HintKind, HintFormat);
                    if (Res != null)
                        return new LocatedImage(Res.Content, Res.Kind, Res.Format,
                            (Res.Compression == CompressionType.IH_COMP_NONE) ? Compressor.Compression : Res.Compression);
                }
            }

            // Composite container: match the first path segment.
            foreach (var Image in Plugins.GetImages())
            {
                if (!Image.DetectImage(Data))
                    continue;

                var Idx = Target.IndexOf('/');
                var Head = (Idx < 0) ? Target : Target.Substring(0, Idx);
                var Rest = (Idx < 0) ? "" : Target.Substring(Idx + 1);

                var Used = new HashSet<string>();
                foreach (var CI in Image.GetImages(Data))
                {
                    if (Unique(CI.Name, Used) != Head)
                        continue;

                    if (CI.Content == null)
                        return null;

                    return LocateNode(new ImageData(CI.Content), Rest, CI.Kind, CI.Format);
                }

                return null;
            }

            return null;
        }

        private void Build(ImageNode Node, ImageData Data, long Size, ImageKind HintKind, string HintFormat, int Depth)
        {
            Node.Size = Size;

            if (Depth > MaxDepth)
                return;

            // Compression is packaging: unpack transparently and keep the same path.
            var Compressor = Plugins.DetectCompressor(Data);
            if (Compressor != null)
            {
                byte[] Unpacked = null;
                try { Unpacked = Compressor.Decompress(Data.ReadArray(0, Data.Length)); } catch { }

                if (Unpacked != null)
                {
                    Node.Compression = Compressor.Compression;
                    Build(Node, new ImageData(Unpacked), Unpacked.Length, HintKind, HintFormat, Depth);
                    return;
                }
            }

            // Composite container.
            foreach (var Image in Plugins.GetImages())
            {
                if (!Image.DetectImage(Data))
                    continue;

                Node.Kind = ImageKind.Unknown;
                Node.Format = Image.Format;
                Node.Container = Image.Format;

                var Used = new HashSet<string>();
                var Contained = Image.GetImages(Data);
                foreach (var CI in Contained)
                {
                    var Child = new ImageNode
                    {
                        Name = Unique(CI.Name, Used),
                        Container = Image.Format
                    };
                    Child.Path = (Node.Path.Length == 0) ? Child.Name : Node.Path + "/" + Child.Name;

                    var ChildSize = (CI.Size >= 0) ? CI.Size : (CI.Content?.Length ?? 0);
                    if (CI.Content != null)
                        Build(Child, new ImageData(CI.Content), ChildSize, CI.Kind, CI.Format, Depth + 1);
                    else
                    {
                        Child.Kind = CI.Kind;
                        Child.Format = CI.Format;
                        Child.Size = ChildSize;
                    }

                    Node.Children.Add(Child);
                }

                return;
            }

            // Leaf detection: kernel / filesystem.
            var Detected = Plugins.DetectImage(Data);
            if (Detected.IsKnown)
            {
                Node.Kind = Detected.Kind;
                Node.Format = Detected.Format;
                return;
            }

            // Plain devicetree.
            if ((Data.Length >= 4) && (Data.ReadUInt32(0) == 0xedfe0dd0u))
            {
                Node.Kind = ImageKind.Devtree;
                Node.Format = "dtb";
                return;
            }

            // Fall back to the kind declared by the container, if any.
            Node.Kind = HintKind;
            Node.Format = HintFormat;
        }

        private static string Unique(string Name, HashSet<string> Used)
        {
            if (string.IsNullOrEmpty(Name))
                Name = "element";

            var Res = Name;
            int i = 1;
            while (!Used.Add(Res))
                Res = Name + "#" + (i++);

            return Res;
        }
    }
}
