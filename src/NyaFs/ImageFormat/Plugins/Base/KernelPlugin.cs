using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.Base
{
    /// <summary>
    /// Base kernel plugin
    /// </summary>
    public class KernelPlugin : NyaPlugin
    {
        public readonly string ImageName;

        private bool Write;
        private bool Read;
        private bool Compression;

        public KernelPlugin(string Name, string ImageName, bool Read, bool Write, bool InternalCompression) : base(Name, "kernel")
        {
            this.ImageName = ImageName;

            this.Read = Read;
            this.Write = Write;

            Compression = InternalCompression;
        }

        /// <summary>
        /// Is filesystem need compression in target image
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
        /// Is image contain this kernel
        /// </summary>
        /// <param name="Image"></param>
        /// <returns></returns>
        public virtual bool DetectKernel(byte[] Image) => false;

        /// <summary>
        /// Get reader
        /// </summary>
        /// <returns></returns>
        public virtual Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data) => null;

        /// <summary>
        /// Get writer
        /// </summary>
        /// <returns></returns>
        public virtual Elements.Kernel.Writer.Writer GetKernelWriter(Elements.Kernel.LinuxKernel Kernel) => null;

        /// <summary>
        /// Check is kernel can save all data
        /// </summary>
        /// <param name="Fs"></param>
        /// <returns></returns>

        public virtual bool CheckKernel(Elements.Kernel.LinuxKernel Fs) => true;
    }
}
