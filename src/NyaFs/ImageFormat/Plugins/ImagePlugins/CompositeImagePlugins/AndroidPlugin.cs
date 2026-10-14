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

            var Img = new LegacyAndroidImage(Image.ReadArray(0, Image.Length));
            if (!Img.IsMagicCorrect || (Img.HeaderVersion >= 3))
                return new ContainedImage[0];

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

            return Res.ToArray();
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
