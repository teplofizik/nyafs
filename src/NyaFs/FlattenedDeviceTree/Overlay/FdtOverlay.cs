using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.FlattenedDeviceTree.Types;

namespace NyaFs.FlattenedDeviceTree
{
    /// <summary>
    /// Applies a device-tree overlay (.dtbo) to a base tree (fdt_overlay_apply-like):
    /// resolves fragment targets (target-path, target phandle, __symbols__/__fixups__),
    /// relocates local phandles using __local_fixups__ and merges __overlay__ nodes.
    /// </summary>
    public static class FdtOverlay
    {
        public static bool TryApply(FlattenedDeviceTree Base, FlattenedDeviceTree Overlay, out string Error)
        {
            Error = null;
            if ((Base == null) || (Overlay == null))
            {
                Error = "Base and overlay trees are required";
                return false;
            }

            var Fragments = new List<Node>();
            foreach (var N in Overlay.Root.Nodes)
                if ((N.Name == "fragment") || N.Name.StartsWith("fragment@", StringComparison.Ordinal))
                    Fragments.Add(N);

            if (Fragments.Count == 0)
            {
                Error = "Not a device-tree overlay: no fragment@N nodes";
                return false;
            }

            var BaseSymbols = Base.Root.GetNode("__symbols__");
            var Fixups = Overlay.Root.GetNode("__fixups__");
            var LocalFixups = Overlay.Root.GetNode("__local_fixups__");

            var OverlayByPath = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var Pair in BuildPathIndex(Overlay.Root))
                if (!OverlayByPath.ContainsKey(Pair.Value))
                    OverlayByPath[Pair.Value] = Pair.Key;

            var BasePathByNode = new Dictionary<Node, string>();
            foreach (var Pair in BuildPathIndex(Base.Root))
                BasePathByNode[Pair.Key] = Pair.Value;

            // Resolve every fragment target before the overlay nodes are mutated.
            var Targets = new List<FragmentTarget>();
            foreach (var Fragment in Fragments)
            {
                if (Fragment.GetNode("__overlay__") == null)
                {
                    Error = $"Overlay {Fragment.Name} has no __overlay__ node";
                    return false;
                }

                var Target = ResolveTarget(Base, BaseSymbols, Fixups, Fragment);
                if (Target == null)
                {
                    Error = $"Cannot resolve target for {Fragment.Name}";
                    return false;
                }

                string BasePath;
                if (!BasePathByNode.TryGetValue(Target, out BasePath))
                    BasePath = null;

                Targets.Add(new FragmentTarget(Fragment, Target, BasePath));
            }

            // Overlays compiled with -@ carry __local_fixups__; relocate their phandles
            // to avoid collisions with the base tree.
            if (LocalFixups != null)
            {
                var Map = new Dictionary<uint, uint>();
                uint Next = GetMaxPhandle(Base.Root) + 1;
                foreach (var Node in Walk(Overlay.Root))
                {
                    var Phandle = GetPhandleProperty(Node);
                    if (Phandle == null)
                        continue;

                    uint Old = FdtValue.ReadUInt32BE(Phandle.Value, 0);
                    uint New = Next++;
                    Map[Old] = New;
                    FdtValue.WriteUInt32BE(Phandle.Value, 0, New);
                }

                if (!ApplyLocalFixups(LocalFixups, OverlayByPath, Map, out Error))
                    return false;
            }

            foreach (var Target in Targets)
                Merge(Target.Node, Target.Fragment.GetNode("__overlay__"));

            MergeSymbols(BaseSymbols, Overlay.Root.GetNode("__symbols__"), Targets);

            return true;
        }

        private sealed class FragmentTarget
        {
            public readonly Node Fragment;
            public readonly Node Node;
            public readonly string BasePath;

            public FragmentTarget(Node Fragment, Node Node, string BasePath)
            {
                this.Fragment = Fragment;
                this.Node = Node;
                this.BasePath = BasePath;
            }
        }

        private static Node ResolveTarget(FlattenedDeviceTree Base, Node BaseSymbols, Node Fixups, Node Fragment)
        {
            var TargetPath = Fragment.GetProperty("target-path");
            if (TargetPath != null)
                return Base.Resolve(NulString(TargetPath.Value));

            var Target = Fragment.GetProperty("target");
            if (Target == null)
                return null;

            var Label = FindExternalLabel(Fixups, Fragment.Name, "target");
            if ((Label != null) && (BaseSymbols != null))
            {
                var Path = BaseSymbols.GetStringValue(Label);
                if (Path != null)
                {
                    var Resolved = Base.Resolve(Path);
                    if (Resolved != null)
                        return Resolved;
                }
            }

            if ((Target.Value == null) || (Target.Value.Length < 4))
                return null;

            uint Phandle = FdtValue.ReadUInt32BE(Target.Value, 0);
            if ((Phandle == 0) || (Phandle == 0xFFFFFFFFu))
                return null;

            return FindByPhandle(Base.Root, Phandle);
        }

        private static string FindExternalLabel(Node Fixups, string NodeName, string PropertyName)
        {
            if (Fixups == null)
                return null;

            var Key = NodeName.Trim('/') + "|" + PropertyName;
            foreach (var Fixup in Fixups.Properties)
            {
                foreach (var Entry in SplitNul(Fixup.Value))
                {
                    var Parts = Entry.Split(':');
                    if (Parts.Length < 2)
                        continue;

                    var Property = Parts[Parts.Length - 2];
                    var NodePath = String.Join(":", Parts, 0, Parts.Length - 2);
                    if ((NodePath.Trim('/') + "|" + Property) == Key)
                        return Fixup.Name;
                }
            }

            return null;
        }

        private static bool ApplyLocalFixups(Node LocalFixups, Dictionary<string, Node> OverlayByPath,
            Dictionary<uint, uint> Map, out string Error)
        {
            Error = null;
            foreach (var Pair in BuildPathIndex(LocalFixups))
            {
                var FixupNode = Pair.Key;
                if ((FixupNode.Properties.Count == 0) || (Pair.Value.Length == 0))
                    continue;

                var TargetPath = Pair.Value;
                if (TargetPath.Length == 0)
                    continue;

                Node TargetNode;
                if (!OverlayByPath.TryGetValue(TargetPath, out TargetNode))
                {
                    Error = $"Overlay local fixup references unknown node {TargetPath}";
                    return false;
                }

                foreach (var Fixup in FixupNode.Properties)
                {
                    var TargetProperty = TargetNode.GetProperty(Fixup.Name);
                    if (TargetProperty == null)
                    {
                        Error = $"Overlay local fixup references unknown property {TargetPath}:{Fixup.Name}";
                        return false;
                    }

                    if ((Fixup.Value.Length % 4) != 0)
                    {
                        Error = $"Invalid overlay local fixup for {TargetPath}:{Fixup.Name}";
                        return false;
                    }

                    for (int Offset = 0; Offset < Fixup.Value.Length; Offset += 4)
                    {
                        uint Position = FdtValue.ReadUInt32BE(Fixup.Value, Offset);
                        if ((Position + 4) > TargetProperty.Value.Length)
                        {
                            Error = $"Overlay local fixup offset out of range for {TargetPath}:{Fixup.Name}";
                            return false;
                        }

                        uint Old = FdtValue.ReadUInt32BE(TargetProperty.Value, (int)Position);
                        uint New;
                        if (Map.TryGetValue(Old, out New))
                            FdtValue.WriteUInt32BE(TargetProperty.Value, (int)Position, New);
                    }
                }
            }

            return true;
        }

        private static void Merge(Node Target, Node Overlay)
        {
            foreach (var Property in Overlay.Properties)
            {
                var Existing = Target.GetProperty(Property.Name);
                if (Existing != null)
                    Existing.Value = Property.Value;
                else
                    Target.Properties.Add(new Property(Property.Name, Property.Value));
            }

            foreach (var Child in Overlay.Nodes)
            {
                var Existing = Target.GetNode(Child.Name);
                if (Existing != null)
                    Merge(Existing, Child);
                else
                    Target.Nodes.Add(Clone(Child));
            }
        }

        private static void MergeSymbols(Node BaseSymbols, Node OverlaySymbols, List<FragmentTarget> Targets)
        {
            if ((BaseSymbols == null) || (OverlaySymbols == null))
                return;

            foreach (var Property in OverlaySymbols.Properties)
            {
                var Translated = TranslateSymbolPath(Targets, NulString(Property.Value));
                if (Translated == null)
                    continue;

                var Raw = Encoding.UTF8.GetBytes(Translated + "\0");
                var Existing = BaseSymbols.GetProperty(Property.Name);
                if (Existing != null)
                    Existing.Value = Raw;
                else
                    BaseSymbols.Properties.Add(new Property(Property.Name, Raw));
            }
        }

        private static string TranslateSymbolPath(List<FragmentTarget> Targets, string Path)
        {
            if (Path == null)
                return null;

            foreach (var Target in Targets)
            {
                var Prefix = "/" + Target.Fragment.Name.Trim('/') + "/__overlay__";
                if ((Path != Prefix) && !Path.StartsWith(Prefix + "/", StringComparison.Ordinal))
                    continue;

                var Root = Target.BasePath ?? "";
                if (Root.Length == 0)
                    Root = "/";
                else if (!Root.StartsWith("/", StringComparison.Ordinal))
                    Root = "/" + Root;

                var Tail = Path.Substring(Prefix.Length);
                if (Tail.Length == 0)
                    return Root;

                return Root.EndsWith("/", StringComparison.Ordinal)
                    ? Root + Tail.TrimStart('/')
                    : Root + Tail;
            }

            return null;
        }

        private static Dictionary<Node, string> BuildPathIndex(Node Root)
        {
            var Result = new Dictionary<Node, string>();
            IndexNode(Result, Root, "");
            return Result;
        }

        private static void IndexNode(Dictionary<Node, string> Index, Node Node, string Path)
        {
            Index[Node] = Path;
            foreach (var Child in Node.Nodes)
                IndexNode(Index, Child, (Path.Length == 0) ? Child.Name : Path + "/" + Child.Name);
        }

        private static IEnumerable<Node> Walk(Node Node)
        {
            yield return Node;
            foreach (var Child in Node.Nodes)
                foreach (var Nested in Walk(Child))
                    yield return Nested;
        }

        private static Property GetPhandleProperty(Node Node) =>
            Node.GetProperty("phandle") ?? Node.GetProperty("linux,phandle");

        private static uint GetMaxPhandle(Node Root)
        {
            uint Max = 0;
            foreach (var Node in Walk(Root))
            {
                var Property = GetPhandleProperty(Node);
                if ((Property == null) || (Property.Value == null) || (Property.Value.Length < 4))
                    continue;

                uint Value = FdtValue.ReadUInt32BE(Property.Value, 0);
                if ((Value != 0xFFFFFFFFu) && (Value > Max))
                    Max = Value;
            }

            return Max;
        }

        private static Node FindByPhandle(Node Root, uint Phandle)
        {
            foreach (var Node in Walk(Root))
            {
                var Property = GetPhandleProperty(Node);
                if ((Property != null) && (Property.Value != null) && (Property.Value.Length >= 4) &&
                    (FdtValue.ReadUInt32BE(Property.Value, 0) == Phandle))
                    return Node;
            }

            return null;
        }

        private static Node Clone(Node Node)
        {
            var Copy = new Node(Node.Name);
            foreach (var Property in Node.Properties)
                Copy.Properties.Add(new Property(Property.Name, (byte[])Property.Value.Clone()));
            foreach (var Child in Node.Nodes)
                Copy.Nodes.Add(Clone(Child));
            return Copy;
        }

        private static IEnumerable<string> SplitNul(byte[] Value)
        {
            if (Value == null)
                yield break;

            int Start = 0;
            for (int i = 0; i < Value.Length; i++)
            {
                if (Value[i] != 0)
                    continue;

                if (i > Start)
                    yield return Encoding.UTF8.GetString(Value, Start, i - Start);
                Start = i + 1;
            }

            if (Value.Length > Start)
                yield return Encoding.UTF8.GetString(Value, Start, Value.Length - Start);
        }

        private static string NulString(byte[] Value)
        {
            if (Value == null)
                return null;

            int Length = Value.Length;
            for (int i = 0; i < Value.Length; i++)
                if (Value[i] == 0)
                {
                    Length = i;
                    break;
                }

            return Encoding.UTF8.GetString(Value, 0, Length);
        }
    }
}
