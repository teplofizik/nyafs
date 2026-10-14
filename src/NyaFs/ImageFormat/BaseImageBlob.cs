using NyaFs.ImageFormat.Elements.Dtb;
using NyaFs.ImageFormat.Elements.Fs;
using NyaFs.ImageFormat.Elements.Kernel;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat
{
    /// <summary>
    /// One element that was loaded into the blob, with hierarchical metadata
    /// (what kind/format it is, what container/compression it came through).
    /// </summary>
    public class LoadedElement
    {
        public readonly ImageKind Kind;
        public readonly string Format;
        public readonly Types.CompressionType Compression;
        public readonly string Container;
        public readonly string Name;

        public LoadedElement(ImageKind Kind, string Format, Types.CompressionType Compression, string Container, string Name)
        {
            this.Kind = Kind;
            this.Format = Format;
            this.Compression = Compression;
            this.Container = Container;
            this.Name = Name;
        }

        public override string ToString() => $"{Kind}/{Format} <- {Container} (comp {Compression})";
    }

    /// <summary>
    /// A part of a container that the converter does not understand. Kept for round-trip:
    /// on save the raw bytes can be re-inserted, while known parts are rebuilt.
    /// </summary>
    public class UnknownImage
    {
        public readonly string Container;
        public readonly long Offset;
        public readonly long Size;

        public UnknownImage(string Container, long Offset, long Size)
        {
            this.Container = Container;
            this.Offset = Offset;
            this.Size = Size;
        }

        public override string ToString() => $"unknown {Size} bytes at {Offset:x} in {Container}";
    }

    public class BaseImageBlob
    {
        public virtual bool IsProvidedDTB => Dtb != null;
        public virtual bool IsProvidedKernel => Kernel != null;
        public virtual bool IsProvidedFs => Fs != null;

        protected DeviceTree  Dtb = null;
        protected LinuxFilesystem  Fs = null;
        protected LinuxKernel Kernel = null;

        /// <summary>
        /// Hierarchical metadata of everything that was loaded into this blob.
        /// </summary>
        public readonly List<LoadedElement> Elements = new List<LoadedElement>();

        /// <summary>
        /// Unknown (not understood) parts of loaded containers, preserved for round-trip.
        /// </summary>
        public readonly List<UnknownImage> Unknown = new List<UnknownImage>();

        public virtual void SetDevTree(int Index, DeviceTree Dtb) => this.Dtb = Dtb;
        public virtual void SetKernel(int Index, LinuxKernel Kernel) => this.Kernel = Kernel;
        public virtual void SetFilesystem(int Index, LinuxFilesystem Fs) => this.Fs = Fs;

        public virtual DeviceTree GetDevTree(int Index = 0) => Dtb;
        public virtual LinuxFilesystem GetFilesystem(int Index = 0) => Fs;
        public virtual LinuxKernel GetKernel(int Index = 0) => Kernel;
    }
}
