using NyaFs.FlattenedDeviceTree;
using NyaFs.ImageFormat.Helper;
using NyaFs.ImageFormat.Types;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Writes a plan tree bottom-up. Clean nodes are inserted as their original
    /// bytes; dirty leaves are serialized and dirty CIMG/FIT parents are rebuilt.
    /// </summary>
    public class ImageBuildPlanWriter
    {
        private readonly ImageBuildPlan Plan;

        public RSA SigningKey { get; set; }
        public string SigningKeyName { get; set; }
        public string SigningAlgorithm { get; set; }

        public ImageBuildPlanWriter(ImageBuildPlan Plan)
        {
            this.Plan = Plan;
        }

        public bool CanWrite => (Plan != null) && (Plan.Root != null) &&
            ((Plan.Root.Format == "cimg") || (Plan.Root.Format == "fit") ||
             ((Plan.Root.Children.Count == 0) && (Plan.Root.Kind != ImageKind.Unknown)));

        public byte[] Build(out string Error)
        {
            Error = null;
            if (!CanWrite)
            {
                Error = "No plan writer for root format '" + (Plan?.Root?.Format ?? "?") + "'";
                return null;
            }

            try
            {
                if (Plan.Root.Format == "fit")
                    return BuildFit(Plan.Root, out Error);
                if (Plan.Root.Format == "cimg")
                    return BuildCImg(Plan.Root, out Error);

                return SerializeLeaf(Plan.Root, out Error);
            }
            catch (Exception E)
            {
                Error = E.Message;
                return null;
            }
        }

        private byte[] BuildCImg(ImageBuildNode Node, out string Error)
        {
            Error = null;
            if (Node.Children.Count != 1)
            {
                Error = "CIMG plan must contain exactly one payload node";
                return null;
            }

            var Payload = BuildNode(Node.Children[0], out Error);
            if (Payload == null)
                return null;

            var Builder = new CImgBuilder
            {
                Label = GetOption(Node, "label", "gpt"),
                ChunkSize = (int)ParseSize(GetOption(Node, "chunk-size", "16M"), 16 * 1024 * 1024),
                Offset = (uint)ParseSize(GetOption(Node, "offset", "0"), 0),
                PartSize = (uint)ParseSize(GetOption(Node, "part-size", "0"), 0)
            };
            return Builder.Build(Payload);
        }

        private byte[] BuildNode(ImageBuildNode Node, out string Error)
        {
            Error = null;

            if (!Node.Dirty && (Node.Raw != null))
                return Node.Raw;

            if (Node.Format == "fit")
                return BuildFit(Node, out Error);

            if (Node.Format == "cimg")
                return BuildCImg(Node, out Error);

            if ((Node.Kind == ImageKind.Unknown) && (Node.Raw != null))
                return Node.Raw;

            return SerializeLeaf(Node, out Error);
        }

        private byte[] BuildFit(ImageBuildNode Node, out string Error)
        {
            Error = null;

            var Images = new List<Tuple<string, ImageBuildNode, byte[], ImageInfo>>();
            var ImageNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var Used = new HashSet<string>();

            foreach (var Child in Node.Children)
            {
                if ((Child.Kind == ImageKind.Unknown) && (Child.Raw == null))
                    continue;
                if ((Child.Kind != ImageKind.Kernel) && (Child.Kind != ImageKind.Filesystem) && (Child.Kind != ImageKind.Devtree) && (Child.Kind != ImageKind.Unknown))
                    continue;

                var Data = BuildNode(Child, out Error);
                if (Data == null)
                    return null;

                var Info = Child.Dirty ? GetInfo(Child) : (Child.Info ?? GetInfo(Child));
                var Name = Unique(Child.Name, Used);
                if (!ImageNames.ContainsKey(Child.Name))
                    ImageNames[Child.Name] = Name;
                Images.Add(Tuple.Create(Name, Child, Data, Info));
            }

            if (Images.Count == 0)
            {
                Error = "FIT plan has no kernel/devicetree/filesystem nodes";
                return null;
            }

            var FitImage = new FlattenedDeviceTree.FlattenedDeviceTree();
            FitImage.Root.AddStringValue("description", Node.Description ?? "U-Boot uImage FDT blobs");

            var ImagesNode = new FlattenedDeviceTree.Types.Node("images");
            ImagesNode.AddUInt32Value("#address-cells", 1);

            foreach (var Item in Images)
            {
                var Name = Item.Item1;
                var Child = Item.Item2;
                var Data = Item.Item3;
                var Info = Item.Item4;

                var ImageNode = new FlattenedDeviceTree.Types.Node(Name);
                ImageNode.AddStringValue("description", GetDescription(Child));
                ImageNode.AddRawValue("data", Data);
                ImageNode.AddStringValue("type", FitHelper.GetType(Info.Type));
                ImageNode.AddStringValue("compression", FitHelper.GetCompression(GetCompression(Child)));
                if (Info.Architecture != CPU.IH_ARCH_INVALID)
                    ImageNode.AddStringValue("arch", FitHelper.GetCPUArchitecture(Info.Architecture));
                if (Info.OperatingSystem != OS.IH_OS_INVALID)
                    ImageNode.AddStringValue("os", FitHelper.GetOperatingSystem(Info.OperatingSystem));

                if ((Child.Kind == ImageKind.Kernel) || (Child.Kind == ImageKind.Devtree) ||
                    (Child.Kind == ImageKind.Filesystem))
                    AddLoadEntry(ImageNode, Info);

                string HashOverride = null;
                Plan.Root.Options.TryGetValue("hash", out HashOverride);
                var Hashes = (!String.IsNullOrEmpty(HashOverride))
                    ? new List<ImageHash> { new ImageHash { Name = "hash@1", Algorithm = HashOverride } }
                    : (Child.Hashes.Count > 0
                        ? new List<ImageHash> { Child.Hashes[0] }
                        : new List<ImageHash> { new ImageHash { Name = "hash@1", Algorithm = "sha1" } });
                foreach (var HashInfo in Hashes)
                {
                    var HashValue = (!Child.Dirty && (HashInfo.Value != null))
                        ? HashInfo.Value
                        : FitHelper.CalcHash(HashInfo.Algorithm, Data);
                    if (HashValue == null)
                        continue;

                    var Hash = new FlattenedDeviceTree.Types.Node(HashInfo.Name);
                    Hash.AddStringValue("algo", HashInfo.Algorithm);
                    Hash.AddRawValue("value", HashValue);
                    ImageNode.Nodes.Add(Hash);
                }

                ImagesNode.Nodes.Add(ImageNode);
            }

            var Configurations = new FlattenedDeviceTree.Types.Node("configurations");
            var Default = !String.IsNullOrEmpty(Node.DefaultConfiguration)
                ? Node.DefaultConfiguration
                : ((Node.Configurations.Count > 0) ? Node.Configurations[0].Name : "config@default");
            Configurations.AddStringValue("default", Default);

            if (Node.Configurations.Count > 0)
            {
                foreach (var SourceConfig in Node.Configurations)
                {
                    var Config = new FlattenedDeviceTree.Types.Node(SourceConfig.Name);
                    if (!String.IsNullOrEmpty(SourceConfig.Description))
                        Config.AddStringValue("description", SourceConfig.Description);
                    AddLink(Config, "kernel", SourceConfig.Kernel, ImageNames);
                    AddLink(Config, "ramdisk", SourceConfig.Ramdisk, ImageNames);
                    AddLink(Config, "fdt", SourceConfig.Fdt, ImageNames);
                    AddLink(Config, "setup", SourceConfig.Setup, ImageNames);
                    AddLink(Config, "fpga", SourceConfig.Fpga, ImageNames);
                    AddLink(Config, "firmware", SourceConfig.Firmware, ImageNames);
                    AddStringList(Config, "loadables", SourceConfig.Loadables, ImageNames);
                    Configurations.Nodes.Add(Config);
                }
            }
            else
            {
                var Config = new FlattenedDeviceTree.Types.Node("config@default");
                Config.AddStringValue("description", "Image package");
                foreach (var Item in Images)
                {
                    if (Item.Item2.Kind == ImageKind.Kernel) Config.AddStringValue("kernel", Item.Item1);
                    if (Item.Item2.Kind == ImageKind.Filesystem) Config.AddStringValue("ramdisk", Item.Item1);
                    if (Item.Item2.Kind == ImageKind.Devtree) Config.AddStringValue("fdt", Item.Item1);
                }
                Configurations.Nodes.Add(Config);
            }

            FitImage.Root.Nodes.Add(ImagesNode);
            FitImage.Root.Nodes.Add(Configurations);

            if (SigningKey == null)
                return new FlattenedDeviceTree.Writer.FDTWriter(FitImage).GetBinary();

            var SignatureNodes = new List<FlattenedDeviceTree.Types.Node>();
            AddFitSignatures(ImagesNode, Configurations, SignatureNodes);

            var Unsigned = new FlattenedDeviceTree.Writer.FDTWriter(FitImage).GetBinary();
            var StringsSize = ReadU32BE(Unsigned, 32);
            foreach (var Signature in SignatureNodes)
                SetSignatureStringsSize(Signature, StringsSize);

            Unsigned = new FlattenedDeviceTree.Writer.FDTWriter(FitImage).GetBinary();
            var Signed = FitSignature.Sign(Unsigned, SigningKey, out _, out var SignError);
            if (Signed == null)
            {
                Error = SignError;
                return null;
            }

            return Signed;
        }

        private void AddFitSignatures(FlattenedDeviceTree.Types.Node ImagesNode,
            FlattenedDeviceTree.Types.Node ConfigurationsNode,
            List<FlattenedDeviceTree.Types.Node> SignatureNodes)
        {
            var Algo = String.IsNullOrEmpty(SigningAlgorithm)
                ? $"sha256,rsa{SigningKey.KeySize}"
                : SigningAlgorithm;
            var KeyName = String.IsNullOrEmpty(SigningKeyName) ? "key" : SigningKeyName;
            var ImageProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "kernel", "ramdisk", "fdt", "setup", "fpga", "firmware", "loadables", "script"
            };

            foreach (var Config in ConfigurationsNode.Nodes)
            {
                var Paths = new List<string> { "/", "/configurations/" + Config.Name };
                foreach (var Property in Config.Properties)
                {
                    if (!ImageProperties.Contains(Property.Name))
                        continue;

                    var Text = Encoding.UTF8.GetString(Property.Value);
                    foreach (var ImageName in Text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var Image = ImagesNode.GetNode(ImageName);
                        if (Image == null)
                            continue;

                        var ImagePath = "/images/" + ImageName;
                        AddUnique(Paths, ImagePath);
                        foreach (var Child in Image.Nodes)
                        {
                            if (Child.Name.StartsWith("hash", StringComparison.Ordinal) ||
                                Child.Name.StartsWith("cipher", StringComparison.Ordinal) ||
                                Child.Name.StartsWith("dm-verity", StringComparison.Ordinal))
                                AddUnique(Paths, ImagePath + "/" + Child.Name);
                        }
                    }
                }

                var Signature = new FlattenedDeviceTree.Types.Node("signature1");
                Signature.AddStringValue("algo", Algo);
                Signature.AddStringValue("key-name-hint", KeyName);
                Signature.AddRawValue("hashed-nodes", Encoding.UTF8.GetBytes(String.Join("\0", Paths) + "\0"));
                Signature.AddRawValue("hashed-strings", new byte[8]);
                Signature.AddRawValue("value", new byte[SigningKey.KeySize / 8]);
                Config.Nodes.Add(Signature);
                SignatureNodes.Add(Signature);
            }
        }

        private static void AddUnique(List<string> Paths, string Path)
        {
            if (!Paths.Contains(Path))
                Paths.Add(Path);
        }

        private static void SetSignatureStringsSize(FlattenedDeviceTree.Types.Node Signature, uint Size)
        {
            foreach (var Property in Signature.Properties)
            {
                if (Property.Name != "hashed-strings")
                    continue;

                var Value = new byte[8];
                Value[4] = (byte)(Size >> 24);
                Value[5] = (byte)(Size >> 16);
                Value[6] = (byte)(Size >> 8);
                Value[7] = (byte)Size;
                Property.Value = Value;
                return;
            }
        }

        private static uint ReadU32BE(byte[] Data, int Offset) =>
            (uint)((Data[Offset] << 24) | (Data[Offset + 1] << 16) | (Data[Offset + 2] << 8) | Data[Offset + 3]);

        private static void AddStringList(FlattenedDeviceTree.Types.Node Config, string Property, System.Collections.Generic.List<string> SourceNames, Dictionary<string, string> ImageNames)
        {
            var Names = new System.Collections.Generic.List<string>();
            foreach (var SourceName in SourceNames)
            {
                string Name;
                if (ImageNames.TryGetValue(SourceName, out Name))
                    Names.Add(Name);
            }
            if (Names.Count > 0)
                Config.AddRawValue(Property, Encoding.UTF8.GetBytes(String.Join("\0", Names) + "\0"));
        }

        private static void AddLink(FlattenedDeviceTree.Types.Node Config, string Property, string SourceName,
            Dictionary<string, string> ImageNames)
        {
            if (String.IsNullOrEmpty(SourceName))
                return;

            string Name;
            if (ImageNames.TryGetValue(SourceName, out Name))
                Config.AddStringValue(Property, Name);
        }

        private byte[] SerializeLeaf(ImageBuildNode Node, out string Error)
        {
            Error = null;
            byte[] Raw = null;

            switch (Node.Kind)
            {
                case ImageKind.Filesystem:
                    if (Node.Fs == null)
                    {
                        Error = "No filesystem object for " + Node.Path;
                        return null;
                    }
                    var FsPlugin = Plugins.PluginHost.Current.ResolveFilesystem(Node.Fs.FilesystemFormat);
                    var FsWriter = FsPlugin?.GetFilesystemWriter(Node.Fs)
                        ?? Elements.Fs.Writer.Writer.GetRawFilesystemWriter(Node.Fs);
                    if ((FsWriter == null) || !FsWriter.CheckFilesystem(Node.Fs))
                    {
                        Error = "Cannot serialize filesystem " + Node.Path;
                        return null;
                    }
                    FsWriter.WriteFs(Node.Fs);
                    if (!FsWriter.HasRawStreamData)
                    {
                        Error = "Cannot serialize filesystem " + Node.Path;
                        return null;
                    }
                    Raw = FsWriter.RawStream;
                    break;

                case ImageKind.Kernel:
                    if (Node.Kernel == null)
                    {
                        Error = "No kernel object for " + Node.Path;
                        return null;
                    }
                    Raw = GetKernelData(Node.Kernel);
                    break;

                case ImageKind.Devtree:
                    if (Node.Dtb == null)
                    {
                        Error = "No devicetree object for " + Node.Path;
                        return null;
                    }
                    Raw = new FlattenedDeviceTree.Writer.FDTWriter(Node.Dtb.DevTree).GetBinary();
                    break;

                default:
                    Error = "Cannot serialize " + Node.Kind + " at " + Node.Path;
                    return null;
            }

            return FitHelper.GetCompressedData(Raw, GetCompression(Node));
        }

        private static byte[] GetKernelData(Elements.Kernel.LinuxKernel Kernel)
        {
            if (!String.IsNullOrEmpty(Kernel.KernelFormat))
            {
                var Plugin = Plugins.PluginHost.Current.ResolveKernel(Kernel.KernelFormat);
                if ((Plugin != null) && Plugin.SupportWrite)
                {
                    var Writer = Plugin.GetKernelWriter(Kernel);
                    if (Writer != null)
                    {
                        Writer.WriteKernel(Kernel);
                        if (Writer.HasRawStreamData)
                            return Writer.RawStream;
                    }
                }
            }

            return Kernel.Image;
        }

        private static CompressionType GetCompression(ImageBuildNode Node)
        {
            if ((Node.Compression == CompressionType.IH_COMP_NONE) && (Node.Info != null))
                return Node.Info.Compression;

            return Node.Compression;
        }

        private static ImageInfo GetInfo(ImageBuildNode Node)
        {
            if (Node.Kernel != null) return Node.Kernel.Info;
            if (Node.Fs != null) return Node.Fs.Info;
            if (Node.Dtb != null) return Node.Dtb.Info;
            return new ImageInfo();
        }

        private static void AddLoadEntry(FlattenedDeviceTree.Types.Node ImageNode, ImageInfo Info)
        {
            if (FitHelper.Is64BitArchitecture(Info.Architecture))
            {
                ImageNode.AddUInt64Value("load", Info.DataLoadAddress);
                ImageNode.AddUInt64Value("entry", Info.EntryPointAddress);
            }
            else
            {
                ImageNode.AddUInt32Value("load", Convert.ToUInt32(Info.DataLoadAddress & 0xFFFFFFFFUL));
                ImageNode.AddUInt32Value("entry", Convert.ToUInt32(Info.EntryPointAddress & 0xFFFFFFFFUL));
            }
        }

        private static string GetDescription(ImageBuildNode Node)
        {
            if (!String.IsNullOrEmpty(Node.Description))
                return Node.Description;

            switch (Node.Kind)
            {
                case ImageKind.Kernel: return "Linux kernel";
                case ImageKind.Devtree: return "Flattened Device Tree blob";
                case ImageKind.Filesystem: return "Ramdisk";
                default: return Node.Name;
            }
        }

        private static string GetOption(ImageBuildNode Node, string Name, string DefaultValue)
        {
            string Value;
            return Node.Options.TryGetValue(Name, out Value) ? Value : DefaultValue;
        }

        private static long ParseSize(string Value, long Default)
        {
            if (String.IsNullOrWhiteSpace(Value))
                return Default;

            var Text = Value.Trim();
            long Multiplier = 1;
            var Last = Char.ToUpperInvariant(Text[Text.Length - 1]);
            if ((Last == 'K') || (Last == 'M') || (Last == 'G'))
            {
                Multiplier = (Last == 'K') ? 1024L : ((Last == 'M') ? 1024L * 1024 : 1024L * 1024 * 1024);
                Text = Text.Substring(0, Text.Length - 1);
            }

            long Result;
            if (Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!Int64.TryParse(Text.Substring(2), NumberStyles.HexNumber, null, out Result))
                    return Default;
            }
            else if (!Int64.TryParse(Text, out Result))
                return Default;

            return Result * Multiplier;
        }

        private static string Unique(string Name, HashSet<string> Used)
        {
            if (String.IsNullOrEmpty(Name))
                Name = "element";

            var Result = Name;
            var Index = 1;
            while (!Used.Add(Result))
                Result = Name + "@" + (Index++);

            return Result;
        }
    }
}
