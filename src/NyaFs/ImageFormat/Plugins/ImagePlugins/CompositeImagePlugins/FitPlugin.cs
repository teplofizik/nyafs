
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
        public override bool DetectImage(byte[] Image)
        {
            return (Image != null) && (Image.Length >= 4) && (Image.ReadUInt32(0) == 0xedfe0dd0u);
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
