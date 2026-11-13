using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Elements.Fs.Reader
{
    public class AndroidReader : Reader
    {
        Types.Android.LegacyAndroidImage Image;
        readonly byte[] Raw;

        public AndroidReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }
        public AndroidReader(byte[] Raw)
        {
            this.Raw = Raw;
            Image = new Types.Android.LegacyAndroidImage(Raw);
        }

        public override void ReadToFs(LinuxFilesystem Dst)
        {
            if (Image.IsMagicCorrect)
            {
                var Version = Image.HeaderVersion;
                if(Version < 3)
                {
                    ReadToFsv0(Dst);
                }
                else if (Version == 3)
                {
                    ReadToFsv3(Dst);
                }
                else
                {
                    throw new NotImplementedException("Android image v4 is not supported yet!");
                }
            }
        }

        private void ReadToFsv3(LinuxFilesystem Dst)
        {
            var V3 = new Types.Android.AndroidImageV3(Raw);
            var RD = V3.Ramdisk;
            if (RD.Length > 0)
            {
                var Comp = (RD.ReadUInt32(0) == 0x04224D18)
                    ? Types.CompressionType.IH_COMP_LZ4
                    : Helper.FitHelper.DetectCompression(RD);
                var Uncompressed = Helper.FitHelper.GetDecompressedData(RD, Comp);
                Dst.Info.Compression = Comp;
                Dst.Info.Type = Types.ImageType.IH_TYPE_RAMDISK;
                Dst.Info.OperatingSystem = Types.OS.IH_OS_LINUX;
                DetectAndRead(Dst, Uncompressed);
            }
        }
        private void ReadToFsv0(LinuxFilesystem Dst)
        {
            var RD = Image.Ramdisk;
            if (RD.Length > 0)
            {
                // Is legacy image...
                uint Magic = RD.ReadUInt32(0);
                if (Magic == 0x56190527)
                {
                    // Parse as legacy
                    var Reader = new LegacyReader(RD);
                    Reader.ReadToFs(Dst);
                }
                else
                {
                    var Comp = (Magic == 0x04224D18)
                        ? Types.CompressionType.IH_COMP_LZ4
                        : Helper.FitHelper.DetectCompression(RD);

                    var Uncompressed = Helper.FitHelper.GetDecompressedData(RD, Comp);

                    Dst.Info.Compression = Comp;
                    Dst.Info.Type = Types.ImageType.IH_TYPE_RAMDISK;
                    Dst.Info.OperatingSystem = Types.OS.IH_OS_LINUX;
                    Dst.Info.DataLoadAddress = Image.RamdiskAddress;
                    DetectAndRead(Dst, Uncompressed);
                }
            }
        }
    }
}
