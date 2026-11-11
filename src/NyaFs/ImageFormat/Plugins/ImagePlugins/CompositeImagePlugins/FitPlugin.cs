using NyaFs.ImageFormat.Plugins.Base;
using NyaFs.ImageFormat.Types;
using NyaIO.Data;
using System;
using System.Collections.Generic;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    public class FitPlugin : CompositeImagePlugin
    {
        public FitPlugin() : base("image.fit", "fit") { }

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
                AddNodeImage(Res, Fit, Configuration.GetStringValue("kernel"), ImageKind.Kernel, Added);
                AddNodeImage(Res, Fit, Configuration.GetStringValue("ramdisk"), ImageKind.Filesystem, Added);
                AddNodeImage(Res, Fit, Configuration.GetStringValue("fdt"), ImageKind.Devtree, Added);
            }
            return Res.ToArray();
        }

        private static void AddNodeImage(List<ContainedImage> Res, FlattenedDeviceTree.FlattenedDeviceTree Fit,
            string NodeName, ImageKind Kind, HashSet<string> Added)
        {
            if ((NodeName == null) || !Added.Add(NodeName))
                return;

            var Node = Fit.Get("images/" + NodeName);
            if (Node == null)
                return;

            var Data = Node.GetValue("data");
            if (Data == null)
                return;

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

            Res.Add(new ContainedImage(NodeName, Kind, "fit", -1, Data.Length, Data, Compression, Info,
                Node.GetStringValue("description"), Hashes.ToArray()));
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
                Res.Add(new ImageContainerConfiguration
                {
                    Name = C.Name,
                    Description = C.GetStringValue("description"),
                    Kernel = C.GetStringValue("kernel"),
                    Ramdisk = C.GetStringValue("ramdisk"),
                    Fdt = C.GetStringValue("fdt")
                });
            }
            return Res.ToArray();
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
