using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>Content source of one plan element.</summary>
    public class PlanContent
    {
        public string File;
        public string Dir;
        public string Blob;
        public bool Empty;
        public string Compression;
        public readonly Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One element of a build layout: a slot in its parent plus the description of what
    /// has to be placed there. The core only understands the envelope; format-specific
    /// keys are passed through <see cref="Options"/> to the plugin.
    /// </summary>
    public class PlanElement
    {
        public string Name = "";
        public string Path = "";
        public long? Offset;
        public long? Size;
        public bool SizeAuto;
        public long? Align;
        public int RepeatCount = 1;
        public long? RepeatStride;
        public byte Fill = 0xFF;
        public bool Immutable;

        public ImageKind Kind = ImageKind.Unknown;
        public string Format;

        public readonly Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public PlanContent Content;

        public readonly List<PlanElement> Children = new List<PlanElement>();

        public bool IsContainer => Children.Count > 0;

        public override string ToString() => $"{Name} [{Kind}/{Format ?? "?"}]";
    }

    /// <summary>
    /// Declarative image layout (YAML; JSON is accepted because it is a YAML subset).
    /// <see cref="Schema"/> is the format version; unknown scalar keys are preserved in
    /// <see cref="Options"/> so the schema can grow without breaking old readers.
    /// </summary>
    public class PlanDocument
    {
        public const int CurrentSchema = 1;

        public int Schema = CurrentSchema;
        public string Format = "";
        public readonly Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<PlanElement> Children = new List<PlanElement>();

        /// <summary>Root content for filesystem layouts (e.g. `format: squashfs`).</summary>
        public PlanContent Content;

        /// <summary>
        /// Remember where the schema lives so relative content paths and the sidecar
        /// blob cache can be resolved by the writers.
        /// </summary>
        public static void SetSource(ImageBuildNode Root, string SchemaFilename)
        {
            if (Root == null)
                return;

            SetDocumentSource(Root.Options, SchemaFilename);
        }

        /// <summary>
        /// Same as <see cref="SetSource(ImageBuildNode, string)"/> but for the parsed
        /// document, so plugins can resolve relative paths (for example a vendor
        /// template) before the root node exists.
        /// </summary>
        public static void SetDocumentSource(IDictionary<string, string> Options, string SchemaFilename)
        {
            if (Options == null)
                return;

            var Full = System.IO.Path.GetFullPath(SchemaFilename);
            Options["plan.base"] = System.IO.Path.GetDirectoryName(Full) ?? "";
            Options["plan.blobs"] = Full + ".blobs";
        }

        public static PlanDocument Load(string Filename, out string Error)
        {
            try
            {
                return Parse(File.ReadAllText(Filename), out Error);
            }
            catch (Exception E)
            {
                Error = E.Message;
                return null;
            }
        }

        public static PlanDocument Parse(string Text, out string Error)
        {
            Error = null;
            try
            {
                var Stream = new YamlStream();
                using (var Reader = new StringReader(Text))
                    Stream.Load(Reader);

                if (Stream.Documents.Count == 0)
                {
                    Error = "Empty layout document";
                    return null;
                }

                var Root = Stream.Documents[0].RootNode as YamlMappingNode;
                if (Root == null)
                {
                    Error = "Layout root must be a mapping";
                    return null;
                }

                var Doc = new PlanDocument();

                if (TryGet(Root, "schema", out var SchemaNode) && (SchemaNode is YamlScalarNode))
                {
                    var Schema = Scalar(SchemaNode);
                    if (Int32.TryParse(Schema, out var Version))
                        Doc.Schema = Version;
                }

                if (Doc.Schema > CurrentSchema)
                {
                    Error = $"Layout schema {Doc.Schema} is newer than supported ({CurrentSchema})";
                    return null;
                }

                Doc.Format = GetString(Root, "format", "");
                ReadOptions(Root, Doc.Options);

                if (TryGet(Root, "options", out var RootOptions) && (RootOptions is YamlMappingNode RootOptionsMap))
                    ReadOptions(RootOptionsMap, Doc.Options, true);

                if (TryGet(Root, "content", out var Content) && (Content is YamlMappingNode ContentMap))
                    Doc.Content = ParseContent(ContentMap);

                if (TryGet(Root, "children", out var Children) && (Children is YamlSequenceNode Sequence))
                {
                    foreach (var Item in Sequence.Children)
                        Doc.Children.Add(ParseElement(Item, "", Doc.Children.Count));
                }

                return Doc;
            }
            catch (Exception E)
            {
                Error = E.Message;
                return null;
            }
        }

        // ---- parsing ----

        private static PlanElement ParseElement(YamlNode Node, string ParentPath, int Index)
        {
            var Map = Node as YamlMappingNode;
            if (Map == null)
                throw new InvalidOperationException("Layout element must be a mapping");

            var E = new PlanElement();
            E.Name = GetString(Map, "name", "element" + Index);
            E.Path = (ParentPath.Length == 0) ? E.Name : ParentPath + "/" + E.Name;

            E.Offset = GetSize(Map, "offset", out _);

            var Size = GetSize(Map, "size", out var SizeAuto);
            E.Size = Size;
            E.SizeAuto = SizeAuto;

            E.Align = GetSize(Map, "align", out _);
            E.Fill = (byte)(GetSize(Map, "fill", out _) ?? 0xFF);
            E.Immutable = GetBool(Map, "immutable", false);

            E.Kind = ParseKind(GetString(Map, "kind", null));
            E.Format = GetString(Map, "format", null);

            if (TryGet(Map, "repeat", out var Repeat) && (Repeat is YamlMappingNode RepeatMap))
            {
                E.RepeatCount = (int)(GetSize(RepeatMap, "count", out _) ?? 1);
                E.RepeatStride = GetSize(RepeatMap, "stride", out _);
            }

            ReadOptions(Map, E.Options);

            if (TryGet(Map, "options", out var Options) && (Options is YamlMappingNode OptionsMap))
                ReadOptions(OptionsMap, E.Options, true);

            if (TryGet(Map, "content", out var Content) && (Content is YamlMappingNode ContentMap))
                E.Content = ParseContent(ContentMap);

            if (TryGet(Map, "children", out var Children) && (Children is YamlSequenceNode Sequence))
            {
                foreach (var Item in Sequence.Children)
                    E.Children.Add(ParseElement(Item, E.Path, E.Children.Count));
            }

            return E;
        }

        private static PlanContent ParseContent(YamlMappingNode Map)
        {
            var C = new PlanContent
            {
                File = GetString(Map, "file", null),
                Dir = GetString(Map, "dir", null),
                Blob = GetString(Map, "blob", null),
                Empty = GetBool(Map, "empty", false),
                Compression = GetString(Map, "compression", null)
            };

            ReadOptions(Map, C.Options, true);
            return C;
        }

        private static ImageKind ParseKind(string Kind)
        {
            if (String.IsNullOrEmpty(Kind))
                return ImageKind.Unknown;

            switch (Kind.Trim().ToLowerInvariant())
            {
                case "kernel": return ImageKind.Kernel;
                case "filesystem":
                case "fs":
                case "ramfs": return ImageKind.Filesystem;
                case "devtree":
                case "dtb":
                case "fdt": return ImageKind.Devtree;
                case "container":
                case "raw": return ImageKind.Unknown;
                default: return ImageKind.Unknown;
            }
        }

        private static void ReadOptions(YamlMappingNode Map, Dictionary<string, string> Target, bool OnlyMap = false)
        {
            foreach (var Pair in Map.Children)
            {
                var Key = Scalar(Pair.Key);
                if (String.IsNullOrEmpty(Key))
                    continue;

                if (Pair.Value is YamlScalarNode)
                {
                    if (!OnlyMap && IsKnownKey(Key))
                        continue;

                    Target[Key] = Scalar(Pair.Value);
                }
            }
        }

        private static bool IsKnownKey(string Key)
        {
            switch (Key.ToLowerInvariant())
            {
                case "name":
                case "schema":
                case "format":
                case "kind":
                case "offset":
                case "size":
                case "align":
                case "fill":
                case "immutable":
                case "repeat":
                case "options":
                case "content":
                case "children":
                    return true;
                default:
                    return false;
            }
        }

        // ---- serialization ----

        public string ToYaml()
        {
            var Sb = new StringBuilder();
            Sb.Append("schema: ").Append(Schema).Append('\n');
            if (!String.IsNullOrEmpty(Format))
                Sb.Append("format: ").Append(Quote(Format)).Append('\n');

            if (Options.Count > 0)
            {
                Sb.Append("options:\n");
                foreach (var O in Sorted(Options))
                {
                    var Value = O.Value ?? "";
                    if (IsNumber(Value))
                        Sb.Append("  ").Append(O.Key).Append(": ").Append(Value).Append('\n');
                    else
                        Sb.Append("  ").Append(O.Key).Append(": ").Append(Quote(Value)).Append('\n');
                }
            }

            if (Children.Count > 0)
            {
                Sb.Append("children:\n");
                foreach (var C in Children)
                    WriteElement(Sb, C, 1);
            }

            return Sb.ToString();
        }

        private static void WriteElement(StringBuilder Sb, PlanElement E, int Indent)
        {
            var Pad = new string(' ', Indent * 2);
            var Pad2 = new string(' ', (Indent + 1) * 2);

            Sb.Append(Pad).Append("- name: ").Append(Quote(E.Name)).Append('\n');

            if (E.Offset.HasValue)
                Sb.Append(Pad2).Append("offset: ").Append(Hex(E.Offset.Value)).Append('\n');

            if (E.SizeAuto)
                Sb.Append(Pad2).Append("size: auto\n");
            else if (E.Size.HasValue)
                Sb.Append(Pad2).Append("size: ").Append(Hex(E.Size.Value)).Append('\n');

            if (E.Align.HasValue)
                Sb.Append(Pad2).Append("align: ").Append(Hex(E.Align.Value)).Append('\n');

            if ((E.RepeatCount > 1) || E.RepeatStride.HasValue)
            {
                Sb.Append(Pad2).Append("repeat: { count: ").Append(E.RepeatCount);
                if (E.RepeatStride.HasValue)
                    Sb.Append(", stride: ").Append(Hex(E.RepeatStride.Value));
                Sb.Append(" }\n");
            }

            if (E.Fill != 0xFF)
                Sb.Append(Pad2).Append("fill: ").Append(Hex(E.Fill)).Append('\n');

            if (E.Immutable)
                Sb.Append(Pad2).Append("immutable: true\n");

            Sb.Append(Pad2).Append("kind: ").Append(KindName(E.Kind)).Append('\n');

            if (!String.IsNullOrEmpty(E.Format))
                Sb.Append(Pad2).Append("format: ").Append(Quote(E.Format)).Append('\n');

            if (E.Options.Count > 0)
            {
                Sb.Append(Pad2).Append("options:\n");
                foreach (var O in Sorted(E.Options))
                    Sb.Append(Pad2).Append("  ").Append(O.Key).Append(": ").Append(Quote(O.Value ?? "")).Append('\n');
            }

            if (E.Content != null)
            {
                Sb.Append(Pad2).Append("content:\n");
                if (E.Content.File != null)
                    Sb.Append(Pad2).Append("  file: ").Append(Quote(E.Content.File)).Append('\n');
                if (E.Content.Dir != null)
                    Sb.Append(Pad2).Append("  dir: ").Append(Quote(E.Content.Dir)).Append('\n');
                if (E.Content.Blob != null)
                    Sb.Append(Pad2).Append("  blob: ").Append(Quote(E.Content.Blob)).Append('\n');
                if (E.Content.Empty)
                    Sb.Append(Pad2).Append("  empty: true\n");
                if (E.Content.Compression != null)
                    Sb.Append(Pad2).Append("  compression: ").Append(Quote(E.Content.Compression)).Append('\n');
            }

            if (E.Children.Count > 0)
            {
                Sb.Append(Pad2).Append("children:\n");
                foreach (var C in E.Children)
                    WriteElement(Sb, C, Indent + 2);
            }
        }

        private static string KindName(ImageKind Kind)
        {
            switch (Kind)
            {
                case ImageKind.Kernel: return "kernel";
                case ImageKind.Filesystem: return "filesystem";
                case ImageKind.Devtree: return "devtree";
                default: return "raw";
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> Sorted(Dictionary<string, string> Map)
        {
            var List = new List<KeyValuePair<string, string>>(Map);
            List.Sort((A, B) => String.Compare(A.Key, B.Key, StringComparison.Ordinal));
            return List;
        }

        private static bool IsNumber(string Value)
        {
            if (String.IsNullOrEmpty(Value))
                return false;
            if (Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return true;
            long Tmp;
            return Int64.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out Tmp);
        }

        private static string Hex(long Value) => "0x" + Value.ToString("x", CultureInfo.InvariantCulture);

        private static string Quote(string Value)
        {
            if (Value == null)
                return "''";
            return "'" + Value.Replace("'", "''") + "'";
        }

        // ---- primitives ----

        private static string Scalar(YamlNode Node) => (Node as YamlScalarNode)?.Value;

        private static bool TryGet(YamlMappingNode Map, string Key, out YamlNode Value)
        {
            foreach (var Pair in Map.Children)
            {
                var Name = Scalar(Pair.Key);
                if (String.Equals(Name, Key, StringComparison.OrdinalIgnoreCase))
                {
                    Value = Pair.Value;
                    return true;
                }
            }

            Value = null;
            return false;
        }

        private static string GetString(YamlMappingNode Map, string Key, string Default) =>
            TryGet(Map, Key, out var Node) ? (Scalar(Node) ?? Default) : Default;

        private static bool GetBool(YamlMappingNode Map, string Key, bool Default)
        {
            if (!TryGet(Map, Key, out var Node))
                return Default;

            var Value = Scalar(Node);
            return Boolean.TryParse(Value, out var Result) ? Result : Default;
        }

        /// <summary>
        /// Parse a size: decimal, 0x hex, or a binary suffix (K/M/G).
        /// "auto" returns null with <paramref name="Auto"/> set.
        /// </summary>
        public static long? ParseSize(string Value, out bool Auto)
        {
            Auto = false;
            if (String.IsNullOrWhiteSpace(Value))
                return null;

            var Text = Value.Trim();
            if (String.Equals(Text, "auto", StringComparison.OrdinalIgnoreCase))
            {
                Auto = true;
                return null;
            }

            long Multiplier = 1;
            var Suffix = Char.ToUpperInvariant(Text[Text.Length - 1]);
            if ((Suffix == 'K') || (Suffix == 'M') || (Suffix == 'G'))
            {
                Multiplier = (Suffix == 'K') ? 1024L : ((Suffix == 'M') ? 1024L * 1024 : 1024L * 1024 * 1024);
                Text = Text.Substring(0, Text.Length - 1).Trim();
            }

            long Result;
            if (Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!Int64.TryParse(Text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out Result))
                    return null;
            }
            else if (!Int64.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out Result))
                return null;

            return Result * Multiplier;
        }

        private static long? GetSize(YamlMappingNode Map, string Key, out bool Auto)
        {
            Auto = false;
            if (!TryGet(Map, Key, out var Node))
                return null;

            return ParseSize(Scalar(Node), out Auto);
        }
    }
}
