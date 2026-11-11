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

        public virtual string DisplayName => ImageName;
        public virtual string[] Aliases => Array.Empty<string>();
        public virtual string[] Extensions => Array.Empty<string>();
        public virtual FilesystemOption[] Options => Array.Empty<FilesystemOption>();

        /// <summary>Metadata read from the container header (for pre-filling options).</summary>
        public virtual IEnumerable<KeyValuePair<string, string>> ReadOptions(ImageData Image) =>
            Array.Empty<KeyValuePair<string, string>>();

        public virtual string ReadDefaultConfiguration(ImageData Image) => null;

        public virtual ImageContainerConfiguration[] ReadConfigurations(ImageData Image) =>
            Array.Empty<ImageContainerConfiguration>();

        public virtual bool TryApplyOption(BaseImageBlob Blob, string Name, string Value, out string Error)
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

            Blob.SetContainerOption(Format, Option.Name, Normalized);
            return true;
        }

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
