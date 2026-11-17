using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Elements.Dtb.Reader
{
    /// <summary>
    /// Device tree wrapped into a U-Boot legacy image (IH_TYPE_FLATDT).
    /// </summary>
    public class LegacyReader : Reader
    {
        private readonly LegacyImage Image;

        public LegacyReader(byte[] Raw)
        {
            Image = new LegacyImage(Raw);
        }

        public override void ReadToDevTree(DeviceTree Dst)
        {
            if (!Image.CorrectHeader || !Image.Correct)
            {
                Log.Error(0, "Invalid legacy device tree image.");
                return;
            }

            if (Image.Type != ImageType.IH_TYPE_FLATDT)
            {
                Log.Error(0, "Legacy image is not a device tree.");
                return;
            }

            byte[] Raw;
            try
            {
                Raw = Helper.FitHelper.GetDecompressedData(Image.ImageData, Image.Compression);
            }
            catch
            {
                Log.Error(0, $"Unsupported legacy device tree compression: {Image.Compression}");
                return;
            }

            var Dtb = new FlattenedDeviceTree.Reader.FDTReader(Raw);
            if (!Dtb.Correct)
            {
                Log.Error(0, "Invalid device tree payload in legacy image.");
                return;
            }

            Dst.DevTree = Dtb.Read();
            Dst.Info.Type = ImageType.IH_TYPE_FLATDT;
            Dst.Info.Compression = CompressionType.IH_COMP_NONE;
            Dst.Info.Architecture = Image.CPUArchitecture;
            Dst.Info.OperatingSystem = Image.OperatingSystem;
            Dst.Info.Name = Image.Name;
        }
    }
}
