using NyaFs.ImageFormat.Elements.Dtb;
using NyaFs.ImageFormat.Elements.Fs;
using NyaFs.ImageFormat.Elements.Kernel;
using System;
using System.Collections.Generic;

namespace NyaFs.ImageFormat
{
    /// <summary>
    /// One node of the loaded image hierarchy, retained for a plan-based repack.
    /// Raw is the element as it was found in its parent (before transparent
    /// decompression), so an untouched subtree can be copied byte for byte.
    /// </summary>
    public class ImageBuildNode
    {
        public string Path = "";
        public string Name = "";
        public string Container = "";
        public ImageKind Kind = ImageKind.Unknown;
        public string Format;
        public Types.CompressionType Compression = Types.CompressionType.IH_COMP_NONE;
        public Types.ImageInfo Info;
        public byte[] Raw;
        public LinuxFilesystem Fs;
        public LinuxKernel Kernel;
        public DeviceTree Dtb;
        public readonly List<ImageBuildNode> Children = new List<ImageBuildNode>();
        public readonly Dictionary<string, string> Options =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Dirty;

        public bool IsContainer => Children.Count > 0;
    }

    /// <summary>
    /// A snapshot of the loaded image tree plus the original bytes. It is the
    /// source for `store &lt;out&gt;` without an explicit format.
    /// </summary>
    public class ImageBuildPlan
    {
        public ImageBuildNode Root { get; }

        public ImageBuildPlan(ImageBuildNode Root)
        {
            this.Root = Root;
        }

        public bool CanPassthrough => (Root != null) && (Root.Raw != null) && !Root.Dirty;

        public ImageBuildNode FindKind(ImageKind Kind) => FindKind(Root, Kind);

        private static ImageBuildNode FindKind(ImageBuildNode Node, ImageKind Kind)
        {
            if (Node == null)
                return null;
            if (Node.Kind == Kind)
                return Node;

            foreach (var Child in Node.Children)
            {
                var Res = FindKind(Child, Kind);
                if (Res != null)
                    return Res;
            }

            return null;
        }

        public bool MarkDirty(ImageKind Kind) => MarkKind(Root, Kind);

        private static bool MarkKind(ImageBuildNode Node, ImageKind Kind)
        {
            if (Node == null)
                return false;

            var Found = Node.Kind == Kind;
            foreach (var Child in Node.Children)
                if (MarkKind(Child, Kind))
                    Found = true;

            if (Found)
                Node.Dirty = true;

            return Found;
        }

        public ImageBuildNode Find(string Path)
        {
            var Target = (Path ?? "").Trim('/');
            var Exact = Find(Root, Target);
            if (Exact != null)
                return Exact;

            return FindAlias(Root, Target.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries), 0);
        }

        private static ImageBuildNode FindAlias(ImageBuildNode Node, string[] Parts, int Index)
        {
            if (Node == null)
                return null;
            if (Index >= Parts.Length)
                return Node;

            var Head = Parts[Index];
            if (Head == "all")
                return FindAlias(Node, Parts, Index + 1);

            if ((Head == Node.Format) || (Head == Node.Container))
                return FindAlias(Node, Parts, Index + 1);

            foreach (var Child in Node.Children)
            {
                if (!AliasMatches(Child, Head))
                    continue;

                var Res = FindAlias(Child, Parts, Index + 1);
                if (Res != null)
                    return Res;
            }

            return null;
        }

        private static bool AliasMatches(ImageBuildNode Node, string Head)
        {
            if (Node.Name == Head)
                return true;
            if ((Node.Format == Head) || (Node.Container == Head))
                return true;

            switch (Node.Kind)
            {
                case ImageKind.Kernel:
                    return (Head == "kernel") || (Head == "zimage");
                case ImageKind.Devtree:
                    return (Head == "devtree") || (Head == "dtb") || (Head == "fdt");
                case ImageKind.Filesystem:
                    return (Head == "filesystem") || (Head == "fs") || (Head == "ramfs") || (Head == "ramdisk");
                default:
                    return false;
            }
        }

        private static ImageBuildNode Find(ImageBuildNode Node, string Target)
        {
            if (Node == null)
                return null;
            if ((Target.Length == 0) || (Node.Path == Target))
                return Node;

            foreach (var Child in Node.Children)
            {
                var Res = Find(Child, Target);
                if (Res != null)
                    return Res;
            }

            return null;
        }

        public bool MarkDirty(string Path)
        {
            var Chain = new List<ImageBuildNode>();
            if (!FindChain(Root, (Path ?? "").Trim('/'), Chain))
                return false;

            foreach (var Node in Chain)
                Node.Dirty = true;
            return true;
        }

        public void MarkAllDirty() => MarkAll(Root);

        private static void MarkAll(ImageBuildNode Node)
        {
            if (Node == null)
                return;

            Node.Dirty = true;
            foreach (var Child in Node.Children)
                MarkAll(Child);
        }

        private static bool FindChain(ImageBuildNode Node, string Target, List<ImageBuildNode> Chain)
        {
            if (Node == null)
                return false;

            Chain.Add(Node);
            if ((Target.Length == 0) || (Node.Path == Target))
                return true;

            foreach (var Child in Node.Children)
            {
                if (FindChain(Child, Target, Chain))
                    return true;
            }

            Chain.RemoveAt(Chain.Count - 1);
            return false;
        }

        public static ImageBuildPlan From(ImageNode Tree)
        {
            return new ImageBuildPlan(Convert(Tree));
        }

        private static ImageBuildNode Convert(ImageNode Source)
        {
            if (Source == null)
                return null;

            var Node = new ImageBuildNode
            {
                Path = Source.Path,
                Name = Source.Name,
                Container = Source.Container,
                Kind = Source.Kind,
                Format = Source.Format,
                Compression = Source.Compression,
                Info = Source.Info,
                Raw = Source.Raw
            };

            foreach (var Option in Source.Options)
                Node.Options[Option.Key] = Option.Value;

            foreach (var Child in Source.Children)
                Node.Children.Add(Convert(Child));

            return Node;
        }
    }
}
