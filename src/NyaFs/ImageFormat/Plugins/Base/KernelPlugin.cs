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

        public ImageKind Kind => ImageKind.Kernel;

        public string Format => ImageName;

        public virtual string DisplayName => ImageName;
        public virtual string[] Aliases => Array.Empty<string>();
        public virtual string[] Extensions => Array.Empty<string>();
        public virtual FilesystemOption[] Options => Array.Empty<FilesystemOption>();

        public virtual bool TryApplyOption(Elements.Kernel.LinuxKernel Kernel, string Name, string Value, out string Error)
        {
            Error = null;
            FilesystemOption Option = null;
            foreach (var O in Options)
                if (string.Equals(O.Name, Name, StringComparison.OrdinalIgnoreCase)) { Option = O; break; }

            if (Option == null)
            {
                Error = "Unknown option '" + Name + "' for " + Format;
                return false;
            }

            string Normalized;
            if (!PluginOptionValidation.Validate(Option, Value, out Normalized, out Error))
                return false;

            Kernel.SetKernelOption(Format, Option.Name, Normalized);
            return true;
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
        /// Detect whether the data contains this kernel image
        /// </summary>
        public virtual bool DetectKernel(ImageData Image) => false;

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
