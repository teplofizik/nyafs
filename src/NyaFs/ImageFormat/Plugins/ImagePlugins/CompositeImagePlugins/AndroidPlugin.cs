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
        public override bool DetectImage(byte[] Image)
        {
            return LegacyAndroidImage.IsAndroidImage(Image);
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
