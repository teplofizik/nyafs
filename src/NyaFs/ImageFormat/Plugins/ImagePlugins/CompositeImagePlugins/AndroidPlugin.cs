using NyaFs.ImageFormat.Plugins.Base;
using NyaFs.ImageFormat.Types.Android;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositePlugins
{
    class AndroidPlugin : CompositeImagePlugin
    {
        public AndroidPlugin() : base("image.android", "android")
        {

        }

        public override FilesystemOption[] Options => new[]
        {
            new FilesystemOption("header-version", FilesystemOptionType.Integer, "0", "Android boot header version (0-2)"),
            new FilesystemOption("page-size", FilesystemOptionType.Integer, "2048", "Android boot page size"),
            new FilesystemOption("name", FilesystemOptionType.String, "", "Image name"),
            new FilesystemOption("cmdline", FilesystemOptionType.String, "", "Kernel command line"),
            new FilesystemOption("extra-cmdline", FilesystemOptionType.String, "", "Extra kernel command line"),
            new FilesystemOption("kernel-addr", FilesystemOptionType.String, null, "Kernel load address"),
            new FilesystemOption("ramdisk-addr", FilesystemOptionType.String, null, "Ramdisk load address"),
            new FilesystemOption("second-addr", FilesystemOptionType.String, null, "Second stage load address"),
            new FilesystemOption("tags-addr", FilesystemOptionType.String, null, "Tags address"),
            new FilesystemOption("recovery-dtbo-addr", FilesystemOptionType.String, null, "Recovery DTBO address"),
            new FilesystemOption("dtb-addr", FilesystemOptionType.String, null, "DTB address")
        };

        /// <summary>
        /// Is image contain this image
        /// </summary>
        /// <param name="Image"></param>
        /// <returns></returns>
        public override bool DetectImage(ImageData Image)
        {
            if (Image.Length < 8) return false;

            for (int i = 0; i < 8; i++)
                if (Image.ReadByte(i) != "ANDROID!"[i])
                    return false;

            return true;
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            if (Image.Length < 0x670)
                return new ContainedImage[0];

            var Raw = Image.ReadArray(0, Image.Length);
            var Img = new LegacyAndroidImage(Raw);
            if (!Img.IsMagicCorrect)
                return new ContainedImage[0];

            if (Img.HeaderVersion == 3)
            {
                var V3 = new AndroidImageV3(Raw);
                var V3Res = new List<ContainedImage>();
                var V3Kernel = V3.Kernel;
                if ((V3Kernel != null) && (V3Kernel.Length > 0))
                    V3Res.Add(new ContainedImage("kernel", ImageKind.Kernel, "android", -1, V3Kernel.Length, V3Kernel));
                var V3Ramdisk = V3.Ramdisk;
                if ((V3Ramdisk != null) && (V3Ramdisk.Length > 0))
                    V3Res.Add(new ContainedImage("ramdisk", ImageKind.Filesystem, "android", -1, V3Ramdisk.Length, V3Ramdisk));
                return V3Res.ToArray();
            }
            if (Img.HeaderVersion >= 4)
                return new ContainedImage[0];

            var V1 = (Img.HeaderVersion >= 1) ? new AndroidImagev1(Raw) : null;
            var V2 = (Img.HeaderVersion >= 2) ? new AndroidImagev2(Raw) : null;
            var Res = new List<ContainedImage>();

            var Kernel = Img.Kernel;
            if ((Kernel != null) && (Kernel.Length > 0))
                Res.Add(new ContainedImage("kernel", ImageKind.Kernel, "android", -1, Kernel.Length, Kernel));

            var Ramdisk = Img.Ramdisk;
            if ((Ramdisk != null) && (Ramdisk.Length > 0))
                Res.Add(new ContainedImage("ramdisk", ImageKind.Filesystem, "android", -1, Ramdisk.Length, Ramdisk));

            var Second = Img.Second;
            if ((Second != null) && (Second.Length > 0))
                Res.Add(new ContainedImage("second", ImageKind.Unknown, null, -1, Second.Length, Second));

            if (V1 != null)
            {
                var Recovery = V1.RecoveryDtbo;
                if ((Recovery != null) && (Recovery.Length > 0))
                    Res.Add(new ContainedImage("recovery_dtbo", ImageKind.Unknown, null, -1, Recovery.Length, Recovery));
            }
            if (V2 != null)
            {
                var DtbImage = V2.Dtbo;
                if ((DtbImage != null) && (DtbImage.Length > 0))
                    Res.Add(new ContainedImage("dtb", ImageKind.Unknown, null, -1, DtbImage.Length, DtbImage));
            }

            return Res.ToArray();
        }

        public override IEnumerable<KeyValuePair<string, string>> ReadOptions(ImageData Image)
        {
            if (Image.Length < 0x670)
                yield break;

            var Raw = Image.ReadArray(0, Image.Length);
            var Img = new LegacyAndroidImage(Raw);
            if (!Img.IsMagicCorrect)
                yield break;

            if (Img.HeaderVersion == 3)
            {
                var V3 = new AndroidImageV3(Raw);
                yield return new KeyValuePair<string, string>("header-version", "3");
                yield return new KeyValuePair<string, string>("page-size", "4096");
                yield return new KeyValuePair<string, string>("cmdline", (V3.Cmdline ?? "").TrimEnd('\0'));
                yield return new KeyValuePair<string, string>("os-version", V3.OSVersion.ToString());
                yield break;
            }
            if (Img.HeaderVersion >= 4)
                yield break;

            yield return new KeyValuePair<string, string>("header-version", Img.HeaderVersion.ToString());
            yield return new KeyValuePair<string, string>("page-size", Img.PageSize.ToString());
            yield return new KeyValuePair<string, string>("name", (Img.Name ?? "").TrimEnd('\0'));
            yield return new KeyValuePair<string, string>("cmdline", (Img.BootAgrs ?? "").TrimEnd('\0'));
            yield return new KeyValuePair<string, string>("extra-cmdline", (Img.ExtraAgrs ?? "").TrimEnd('\0'));
            yield return new KeyValuePair<string, string>("kernel-addr", "0x" + Img.KernelAddress.ToString("x"));
            yield return new KeyValuePair<string, string>("ramdisk-addr", "0x" + Img.RamdiskAddress.ToString("x"));
            yield return new KeyValuePair<string, string>("second-addr", "0x" + Img.SecondAddress.ToString("x"));
            yield return new KeyValuePair<string, string>("tags-addr", "0x" + Img.TagsAddress.ToString("x"));
            yield return new KeyValuePair<string, string>("os-version", Img.OSVersion.ToString());

            if (Img.HeaderVersion >= 1)
            {
                var V1 = new AndroidImagev1(Raw);
                yield return new KeyValuePair<string, string>("recovery-dtbo-addr", "0x" + V1.RecoveryDtboAddress.ToString("x"));
            }
            if (Img.HeaderVersion >= 2)
            {
                var V2 = new AndroidImagev2(Raw);
                yield return new KeyValuePair<string, string>("dtb-addr", "0x" + V2.DtboAddress.ToString("x"));
            }
        }

        /// <summary>
        /// Get reader
        /// </summary>
        /// <returns></returns>
        public override Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data) => 
            new ImageFormat.Elements.Kernel.Reader.AndroidReader(Data);

        /// <summary>
        /// Get reader
        /// </summary>
        /// <returns></returns>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) => 
            new ImageFormat.Elements.Fs.Reader.AndroidReader(Data);
    }
}
