using NyaFs.ImageFormat.Plugins.Base;
using NyaFs.ImageFormat.Types;
using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    public class FitPlugin : CompositeImagePlugin
    {
        public FitPlugin() : base("image.fit", "fit") { }

        public override NyaFs.ImageFormat.Plugins.Base.FilesystemOption[] Options => new[]
        {
            new NyaFs.ImageFormat.Plugins.Base.FilesystemOption("hash", NyaFs.ImageFormat.Plugins.Base.FilesystemOptionType.Enum, "sha1", "FIT hash algorithm", new[] { "sha1", "sha256", "sha512", "crc32" }),
            new NyaFs.ImageFormat.Plugins.Base.FilesystemOption("sign", NyaFs.ImageFormat.Plugins.Base.FilesystemOptionType.String, null, "KeyStore profile used to sign FIT configurations (or active)"),
            new NyaFs.ImageFormat.Plugins.Base.FilesystemOption("sign-algo", NyaFs.ImageFormat.Plugins.Base.FilesystemOptionType.String, "sha256,rsa2048", "FIT signature algorithm"),
            new NyaFs.ImageFormat.Plugins.Base.FilesystemOption("sign-key-name", NyaFs.ImageFormat.Plugins.Base.FilesystemOptionType.String, null, "FIT key-name-hint"),
            new NyaFs.ImageFormat.Plugins.Base.FilesystemOption("sign-control", NyaFs.ImageFormat.Plugins.Base.FilesystemOptionType.String, null, "Write U-Boot control DTB with the signing public key")
        };

        public override bool DetectImage(ImageData Image)
        {
            if ((Image.Length < 0x28) || (Image.ReadUInt32(0) != 0xedfe0dd0u))
                return false;

            var Reader = new FlattenedDeviceTree.Reader.FDTReader(Image.ReadArray(0, Image.Length));
            if (!Reader.Correct)
                return false;

            var Fit = Reader.Read();
            return Fit.HasNode("configurations") && Fit.HasNode("images");
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            var Fit = ReadFit(Image);
            if (Fit == null)
                return new ContainedImage[0];

            var Configurations = Fit.Get("configurations");
            if (Configurations == null)
                return new ContainedImage[0];

            var Res = new List<ContainedImage>();
            var Added = new HashSet<string>();
            foreach (var Configuration in Configurations.Nodes)
            {
                AddNodeImage(Res, Image, Fit, Configuration.GetStringValue("kernel"), ImageKind.Kernel, Added);
                AddNodeImage(Res, Image, Fit, Configuration.GetStringValue("ramdisk"), ImageKind.Filesystem, Added);
                AddNodeImage(Res, Image, Fit, Configuration.GetStringValue("fdt"), ImageKind.Devtree, Added);
                AddNodeImage(Res, Image, Fit, Configuration.GetStringValue("setup"), ImageKind.Unknown, Added);
                AddNodeImage(Res, Image, Fit, Configuration.GetStringValue("fpga"), ImageKind.Unknown, Added);
                AddNodeImage(Res, Image, Fit, Configuration.GetStringValue("firmware"), ImageKind.Unknown, Added);
                foreach (var Loadable in ReadStrings(Configuration.GetValue("loadables")))
                    AddNodeImage(Res, Image, Fit, Loadable, ImageKind.Unknown, Added);
            }
            return Res.ToArray();
        }

        private static void AddNodeImage(List<ContainedImage> Res, ImageData Image, FlattenedDeviceTree.FlattenedDeviceTree Fit,
            string NodeName, ImageKind Kind, HashSet<string> Added)
        {
            if ((NodeName == null) || !Added.Add(NodeName))
                return;

            var Node = Fit.Get("images/" + NodeName);
            if (Node == null)
                return;

            var Data = Node.GetValue("data");
            var External = false;
            if (Data == null)
            {
                Data = ReadExternalData(Image, Node);
                External = Data != null;
            }
            if (Data == null)
            {
                var Position = Node.GetValue("data-position");
                if (Position != null)
                    NyaFs.Log.Warning(0, "FIT image '" + NodeName + "' uses data-position (a memory/device address); payload is not inside the FIT and cannot be loaded.");
                else
                    NyaFs.Log.Warning(0, "FIT image '" + NodeName + "' has no embedded data; external payload is not available.");
                return;
            }

            var Compression = Helper.FitHelper.GetCompression(
                Node.GetStringValue("compression") ?? "none");

            var Info = new ImageInfo
            {
                Name = NodeName,
                Architecture = Helper.FitHelper.GetCPUArchitecture(Node.GetStringValue("arch")),
                OperatingSystem = Helper.FitHelper.GetOperatingSystem(Node.GetStringValue("os")),
                Type = Helper.FitHelper.GetType(Node.GetStringValue("type")),
                Compression = Compression
            };

            var Load = Node.GetValue("load");
            var Entry = Node.GetValue("entry");
            if (Load != null)
                Info.DataLoadAddress = (Load.Length == 8) ? Load.ReadUInt64BE(0) : Load.ReadUInt32BE(0);
            if (Entry != null)
                Info.EntryPointAddress = (Entry.Length == 8) ? Entry.ReadUInt64BE(0) : Entry.ReadUInt32BE(0);

            var Hashes = new List<ImageHash>();
            foreach (var HashNode in Node.Nodes)
            {
                if (!HashNode.Name.StartsWith("hash@"))
                    continue;
                Hashes.Add(new ImageHash
                {
                    Name = HashNode.Name,
                    Algorithm = HashNode.GetStringValue("algo") ?? "sha1",
                    Value = HashNode.GetValue("value")
                });
            }

            var LeafFormat = (Kind == ImageKind.Devtree) ? "dtb" : "raw";
            Res.Add(new ContainedImage(NodeName, Kind, LeafFormat, -1, Data.Length, Data, Compression, Info,
                Node.GetStringValue("description"), Hashes.ToArray(), External));
        }

        private static byte[] ReadExternalData(ImageData Image, FlattenedDeviceTree.Types.Node Node)
        {
            var OffsetValue = Node.GetValue("data-offset");
            var SizeValue = Node.GetValue("data-size");
            if ((OffsetValue == null) || (SizeValue == null))
                return null;

            var DataOffset = ReadCell(OffsetValue);
            var DataSize = ReadCell(SizeValue);
            if ((DataOffset < 0) || (DataSize < 0) || (DataSize > Int32.MaxValue))
                return null;

            var TotalSize = Image.ReadUInt32BE(4);
            var Base = (long)((TotalSize + 3) & ~3u);
            var Start = Base + DataOffset;
            if ((Start < 0) || (Start + DataSize > Image.Length))
                return null;

            return Image.ReadArray((int)Start, (int)DataSize);
        }

        private static long ReadCell(byte[] Value)
        {
            if (Value.Length == 4)
                return (uint)((Value[0] << 24) | (Value[1] << 16) | (Value[2] << 8) | Value[3]);
            if (Value.Length == 8)
            {
                ulong Result = 0;
                for (var I = 0; I < 8; I++)
                    Result = (Result << 8) | Value[I];
                return (long)Result;
            }
            return -1;
        }
        public override IEnumerable<KeyValuePair<string, string>> ReadOptions(ImageData Image)
        {
            var Fit = ReadFit(Image);
            if ((Fit != null) && HasSignatures(Fit))
                yield return new KeyValuePair<string, string>("signatures", "true");
        }

        private static bool HasSignatures(FlattenedDeviceTree.FlattenedDeviceTree Fit)
        {
            var Images = Fit.Get("images");
            if (Images != null)
            {
                foreach (var Node in Images.Nodes)
                    foreach (var Child in Node.Nodes)
                        if (Child.Name.StartsWith("signature"))
                            return true;
            }

            var Configurations = Fit.Get("configurations");
            if (Configurations != null)
            {
                foreach (var Node in Configurations.Nodes)
                    foreach (var Child in Node.Nodes)
                        if (Child.Name.StartsWith("signature"))
                            return true;
            }

            return false;
        }

        public override string ReadDescription(ImageData Image)
        {
            var Fit = ReadFit(Image);
            return Fit?.Root.GetStringValue("description");
        }

        public override string ReadDefaultConfiguration(ImageData Image)
        {
            var Fit = ReadFit(Image);
            var Configurations = Fit?.Get("configurations");
            return Configurations?.GetStringValue("default");
        }

        public override ImageContainerConfiguration[] ReadConfigurations(ImageData Image)
        {
            var Fit = ReadFit(Image);
            var Configurations = Fit?.Get("configurations");
            if (Configurations == null)
                return Array.Empty<ImageContainerConfiguration>();

            var Res = new List<ImageContainerConfiguration>();
            foreach (var C in Configurations.Nodes)
            {
                var Config = new ImageContainerConfiguration
                {
                    Name = C.Name,
                    Description = C.GetStringValue("description"),
                    Kernel = C.GetStringValue("kernel"),
                    Ramdisk = C.GetStringValue("ramdisk"),
                    Fdt = C.GetStringValue("fdt"),
                    Setup = C.GetStringValue("setup"),
                    Fpga = C.GetStringValue("fpga"),
                    Firmware = C.GetStringValue("firmware")
                };
                foreach (var Loadable in ReadStrings(C.GetValue("loadables")))
                    Config.Loadables.Add(Loadable);
                Res.Add(Config);
            }
            return Res.ToArray();
        }

        private static string[] ReadStrings(byte[] Value)
        {
            if (Value == null)
                return new string[0];
            return Encoding.UTF8.GetString(Value).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static FlattenedDeviceTree.FlattenedDeviceTree ReadFit(ImageData Image)
        {
            if ((Image.Length < 0x28) || (Image.ReadUInt32(0) != 0xedfe0dd0u))
                return null;

            var Reader = new FlattenedDeviceTree.Reader.FDTReader(Image.ReadArray(0, Image.Length));
            return Reader.Correct ? Reader.Read() : null;
        }

        public override Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data) =>
            new Elements.Kernel.Reader.FitReader(Data, "", null);

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.FitReader(Data, "", null);

        public override Elements.Dtb.Reader.Reader GetDtbReader(byte[] Data) =>
            new Elements.Dtb.Reader.FitReader(Data, "", null);
    }
}
