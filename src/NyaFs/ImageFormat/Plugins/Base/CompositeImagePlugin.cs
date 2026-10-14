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

        public string Format => ImageName;

        /// <summary>
        /// Is image contain this image
        /// </summary>
        /// <param name="Image"></param>
        /// <returns></returns>
        public virtual bool DetectImage(ImageData Image) => false;

        /// <summary>
        /// Enumerate the elements contained in this composite image (with declared kind/format).
        /// Used for reporting/metadata and to re-run detection on elements whose type is unknown.
        /// </summary>
        public virtual ContainedImage[] GetImages(ImageData Image) => new ContainedImage[0];

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
