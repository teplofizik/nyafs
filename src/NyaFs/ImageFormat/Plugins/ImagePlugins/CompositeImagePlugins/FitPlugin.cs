
using NyaFs.ImageFormat.Plugins.Base;
using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    public class FitPlugin : CompositeImagePlugin
    {
        public FitPlugin() : base("image.fit", "fit")
        {

        }

        /// <summary>
        /// Is image contain this image
        /// </summary>
        /// <param name="Image"></param>
        /// <returns></returns>
        public override bool DetectImage(ImageData Image)
        {
            if ((Image.Length < 0x28) || (Image.ReadUInt32(0) != 0xedfe0dd0u))
                return false;

            // Distinguish a real FIT from a plain DTB: FIT has 'configurations' and 'images' nodes.
            var Reader = new FlattenedDeviceTree.Reader.FDTReader(Image.ReadArray(0, Image.Length));
            if (!Reader.Correct)
                return false;

            var Fit = Reader.Read();
            return Fit.HasNode("configurations") && Fit.HasNode("images");
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            if ((Image.Length < 0x28) || (Image.ReadUInt32(0) != 0xedfe0dd0u))
                return new ContainedImage[0];

            var Reader = new FlattenedDeviceTree.Reader.FDTReader(Image.ReadArray(0, Image.Length));
            if (!Reader.Correct)
                return new ContainedImage[0];

            var Fit = Reader.Read();
            var Configurations = Fit.Get("configurations");
            if (Configurations == null)
                return new ContainedImage[0];

            var ConfigName = Configurations.GetStringValue("default");
            if ((ConfigName == null) && (Configurations.Nodes.Count > 0))
                ConfigName = Configurations.Nodes[0].Name;
            if (ConfigName == null)
                return new ContainedImage[0];

            var Configuration = Fit.Get("configurations/" + ConfigName);
            if (Configuration == null)
                return new ContainedImage[0];

            var Res = new List<ContainedImage>();
            AddNodeImage(Res, Fit, Configuration.GetStringValue("kernel"), ImageKind.Kernel);
            AddNodeImage(Res, Fit, Configuration.GetStringValue("ramdisk"), ImageKind.Filesystem);
            AddNodeImage(Res, Fit, Configuration.GetStringValue("fdt"), ImageKind.Devtree);
            return Res.ToArray();
        }

        private static void AddNodeImage(List<ContainedImage> Res, FlattenedDeviceTree.FlattenedDeviceTree Fit, string NodeName, ImageKind Kind)
        {
            if (NodeName == null) return;

            var Node = Fit.Get("images/" + NodeName);
            if (Node == null) return;

            var Data = Node.GetValue("data");
            if (Data == null) return;

            var Compression = Helper.FitHelper.GetCompression(
                Node.GetStringValue("compression") ?? "none");

            Res.Add(new ContainedImage(NodeName, Kind, "fit", -1, Data.Length, Data, Compression));
        }

        /// <summary>
        /// Get reader
        /// </summary>
        /// <returns></returns>
        public override Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data) =>
            new Elements.Kernel.Reader.FitReader(Data, "", null);

        /// <summary>
        /// Get reader
        /// </summary>
        /// <returns></returns>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.FitReader(Data, "", null);

        /// <summary>
        /// Get reader
        /// </summary>
        /// <returns></returns>
        public override Elements.Dtb.Reader.Reader GetDtbReader(byte[] Data) =>
            new Elements.Dtb.Reader.FitReader(Data, "", null);
    }
}
