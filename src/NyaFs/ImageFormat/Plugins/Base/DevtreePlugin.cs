using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.Base
{
    /// <summary>
    /// Base device-tree plugin (mirrors <see cref="KernelPlugin"/> / <see cref="FilesystemPlugin"/>).
    /// </summary>
    public class DevtreePlugin : NyaPlugin
    {
        public readonly string ImageName;

        private bool Write;
        private bool Read;
        private bool Compression;

        public DevtreePlugin(string Name, string ImageName, bool Read, bool Write, bool InternalCompression) : base(Name, "devtree")
        {
            this.ImageName = ImageName;

            this.Read = Read;
            this.Write = Write;

            Compression = InternalCompression;
        }

        public ImageKind Kind => ImageKind.Devtree;

        public string Format => ImageName;

        /// <summary>
        /// Is devtree need compression in target image
        /// </summary>
        public virtual bool NeedExternalCompression => !Compression;

        /// <summary>
        /// Is read supported
        /// </summary>
        public virtual bool SupportRead => Read;

        /// <summary>
        /// Is write supported
        /// </summary>
        public virtual bool SupportWrite => Write;

        /// <summary>
        /// Detect whether the data contains this device tree
        /// </summary>
        public virtual bool DetectDevtree(ImageData Image) => false;

        /// <summary>
        /// Get reader
        /// </summary>
        public virtual Elements.Dtb.Reader.Reader GetDevtreeReader(byte[] Data) => null;

        /// <summary>
        /// Get writer
        /// </summary>
        public virtual Elements.Dtb.Writer.Writer GetDevtreeWriter(Elements.Dtb.DeviceTree Dtb) => null;

        /// <summary>
        /// Check is devtree can save all data
        /// </summary>
        public virtual bool CheckDevtree(Elements.Dtb.DeviceTree Dtb) => true;
    }
}
