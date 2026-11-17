using System;
using System.Collections.Generic;
using System.Text;
using ZstdSharp;

namespace NyaFs.ImageFormat.Plugins.Base
{
    [Flags]
    public enum DetectionCapabilities
    {
        None = 0,
        Magic = 1,
        Extension = 2,
        ExplicitFormat = 4
    }

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

        /// <summary>
        /// How this container may be selected. Existing plugins are magic-based by default;
        /// formats without a reliable magic can opt into extension/explicit-format loading.
        /// </summary>
        public virtual DetectionCapabilities DetectCapabilities => DetectionCapabilities.Magic;

        public virtual FilesystemOption[] Options => Array.Empty<FilesystemOption>();

        /// <summary>Metadata read from the container header (for pre-filling options).</summary>
        public virtual IEnumerable<KeyValuePair<string, string>> ReadOptions(ImageData Image) =>
            Array.Empty<KeyValuePair<string, string>>();

        public virtual string ReadDescription(ImageData Image) => null;

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
        /// <summary>Can this composite format be built from the loaded blob.</summary>
        public virtual bool SupportWrite => false;

        /// <summary>Can an empty plan (layout) be created for this format.</summary>
        public virtual bool SupportLayout => false;

        /// <summary>
        /// Create a build plan root from a declarative layout (YAML/JSON). The core only
        /// orchestrates; the plugin validates its options and fills the node tree.
        /// </summary>
        public virtual ImageBuildNode CreatePlan(ImageFormat.Composite.PlanDocument Document, out string Error)
        {
            Error = "Layout is not supported for " + Format;
            return null;
        }

        /// <summary>Keep opaque children in the loaded plan even when no nested reader accepts them.</summary>
        public virtual bool PreserveUnknownChildren => false;

        /// <summary>Build a composite image from the loaded elements (no file I/O).</summary>
        public virtual byte[] Build(BaseImageBlob Blob, out string Error)
        {
            Error = null;
            return null;
        }

        /// <summary>
        /// Build with an explicit plugin registry (used when a layout has to serialize
        /// nested filesystems/kernels by format). Defaults to the legacy entry point.
        /// </summary>
        public virtual byte[] Build(BaseImageBlob Blob, Plugins.PluginRegistry Plugins, out string Error) =>
            Build(Blob, out Error);
    }
}
