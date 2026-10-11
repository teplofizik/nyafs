using System;
using System.Collections.Generic;
using System.Text;
using ZstdSharp;

namespace NyaFs.ImageFormat.Plugins.Base
{
    public class CompositeImagePlugin : NyaPlugin
    {
        public readonly string ImageName;

        public CompositeImagePlugin(string Name, string ImageName) : base(Name, "image")
        {
            this.ImageName = ImageName;
        }

        /// <summary>
        /// Is image contain this image
        /// </summary>
        /// <param name="Image"></param>
        /// <returns></returns>
        public virtual bool DetectImage(byte[] Image) => false;

        /// <summary>
        /// Get kernel reader
        /// </summary>
        /// <returns></returns>
        public virtual Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data) => null;

        /// <summary>
        /// Get filesystem reader
        /// </summary>
        /// <returns></returns>
        public virtual Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) => null;

        /// <summary>
        /// Get device tree reader
        /// </summary>
        /// <returns></returns>
        public virtual Elements.Dtb.Reader.Reader GetDtbReader(byte[] Data) => null;
    }
}
