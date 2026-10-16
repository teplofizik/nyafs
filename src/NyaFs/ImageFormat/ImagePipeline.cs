using System;
using System.Collections.Generic;
using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat
{
    /// <summary>
    /// Plugin-driven recursive loading pipeline.
    ///
    /// For a given byte range it: detects compression (and recurses on the unpacked data),
    /// then composite containers, then kernel / filesystem / devicetree plugins.
    /// Anything unrecognized is preserved as <see cref="UnknownImage"/> for round-trip.
    ///
    /// A caller may pass a declared kind/format (from container metadata) as a hint: if content
    /// detection fails, the hint is used to force-load (e.g. a bare kernel image without magic).
    /// </summary>
    public class ImagePipeline
    {
        private readonly Processor.Scripting.ScriptPlugins Plugins;
        private readonly BaseImageBlob Blob;

        private const int MaxDepth = 32;

        public ImagePipeline(Processor.Scripting.ScriptPlugins Plugins, BaseImageBlob Blob)
        {
            this.Plugins = Plugins;
            this.Blob = Blob;
        }

        public bool Load(byte[] Raw) => LoadElement(new ImageData(Raw), "image", ImageKind.Unknown, null, 0);

        public bool Load(byte[] Raw, ImageKind HintKind, string HintFormat) =>
            LoadElement(new ImageData(Raw), "sub", HintKind, HintFormat, 0);

        private bool LoadElement(ImageData Data, string Container, ImageKind HintKind, string HintFormat, int Depth)
        {
            if (Depth > MaxDepth)
                return false;

            // 1) Compression: unpack and re-run detection on the payload.
            var Compressor = Plugins.DetectCompressor(Data);
            if (Compressor != null)
            {
                byte[] Unpacked = null;
                try { Unpacked = Compressor.Decompress(Data.ReadArray(0, Data.Length)); } catch { }

                if (Unpacked != null)
                {
                    Blob.Elements.Add(new LoadedElement(ImageKind.Unknown, Compressor.CompressorName, Compressor.Compression, Container, Compressor.CompressorName));
                    return LoadElement(new ImageData(Unpacked), Container + ":" + Compressor.CompressorName, HintKind, HintFormat, Depth + 1);
                }
            }

            var Raw = Data.ReadArray(0, Data.Length);

            // 2) Composite containers.
            foreach (var Image in Plugins.GetImages())
            {
                if (!Image.DetectImage(Data))
                    continue;

                bool Any = false;

                // Re-run the pipeline on elements whose type is not declared in the container.
                foreach (var Contained in Image.GetImages(Data))
                {
                    if ((Contained.Kind == ImageKind.Unknown) && (Contained.Content != null))
                    {
                        Blob.Elements.Add(new LoadedElement(ImageKind.Unknown, Contained.Format, Contained.Compression, Container, Image.Name + "/" + Contained.Name));
                        if (LoadElement(new ImageData(Contained.Content), Container + "/" + Image.Name + "/" + Contained.Name, Contained.Kind, Contained.Format, Depth + 1))
                            Any = true;
                    }
                }

                var KernelReader = Image.GetKernelReader(Raw);
                if (KernelReader != null)
                {
                    var Dst = GetKernel();
                    try { KernelReader.ReadToKernel(Dst); } catch { }
                    if (Dst.Loaded)
                    {
                        Blob.Elements.Add(new LoadedElement(ImageKind.Kernel, Image.Format, CompressionType.IH_COMP_NONE, Container, Image.Name));
                        Any = true;
                    }
                }

                var FsReader = Image.GetFilesystemReader(Raw);
                if (FsReader != null)
                {
                    var Dst = GetFs();
                    try { FsReader.ReadToFs(Dst); } catch { }
                    if (Dst.Loaded)
                    {
                        Blob.Elements.Add(new LoadedElement(ImageKind.Filesystem, Image.Format, CompressionType.IH_COMP_NONE, Container, Image.Name));
                        Any = true;
                    }
                }

                var DtbReader = Image.GetDtbReader(Raw);
                if (DtbReader != null)
                {
                    var Dst = GetDtb();
                    try { DtbReader.ReadToDevTree(Dst); } catch { }
                    if (Dst.Loaded)
                    {
                        Blob.Elements.Add(new LoadedElement(ImageKind.Devtree, Image.Format, CompressionType.IH_COMP_NONE, Container, Image.Name));
                        Any = true;
                    }
                }

                if (Any)
                    return true;
            }

            // 3) Kernel.
            foreach (var KernelPlugin in Plugins.GetKernels())
            {
                if (!KernelPlugin.DetectKernel(Data))
                    continue;

                var Reader = KernelPlugin.GetKernelReader(Raw);
                if (Reader != null)
                {
                    var Dst = GetKernel();
                    try { Reader.ReadToKernel(Dst); } catch { }
                    if (Dst.Loaded)
                    {
                        Blob.Elements.Add(new LoadedElement(ImageKind.Kernel, KernelPlugin.Format, Dst.Info.Compression, Container, KernelPlugin.Format));
                        return true;
                    }
                }
            }

            // 4) Filesystem.
            foreach (var FsPlugin in Plugins.GetFilesystems())
            {
                if (!FsPlugin.DetectFilesystem(Data))
                    continue;

                var Reader = FsPlugin.GetFilesystemReader(Raw);
                if (Reader != null)
                {
                    var Dst = GetFs();
                    try { Reader.ReadToFs(Dst); } catch { }
                    if (Dst.Loaded)
                    {
                        Blob.Elements.Add(new LoadedElement(ImageKind.Filesystem, FsPlugin.Format, Dst.Info.Compression, Container, FsPlugin.Format));
                        return true;
                    }
                }
            }

            // 5) Device tree (plain DTB).
            if ((Data.Length >= 4) && (Data.ReadUInt32(0) == 0xedfe0dd0u))
            {
                var Dst = GetDtb();
                try { new Elements.Dtb.Reader.DtbReader(Raw).ReadToDevTree(Dst); } catch { }
                if (Dst.Loaded)
                {
                    Blob.Elements.Add(new LoadedElement(ImageKind.Devtree, "dtb", CompressionType.IH_COMP_NONE, Container, "dtb"));
                    return true;
                }
            }

            // 6) Forced load by declared kind (content detection failed but the type is known).
            // The reader is provided by the plugins (no hardcoded format switches here).
            if (HintKind == ImageKind.Kernel)
            {
                var Plugin = ((HintFormat != null) ? Plugins.GetKernelByFormat(HintFormat) : null)
                          ?? Plugins.GetKernelByFormat("raw");

                var Reader = Plugin?.GetKernelReader(Raw);
                if (Reader != null)
                {
                    var Dst = GetKernel();
                    try { Reader.ReadToKernel(Dst); } catch { }
                    if (Dst.Loaded)
                    {
                        Blob.Elements.Add(new LoadedElement(ImageKind.Kernel, Plugin.Format, Dst.Info.Compression, Container, Plugin.Format));
                        return true;
                    }
                }
            }

            if (HintKind == ImageKind.Filesystem)
            {
                var Plugin = (HintFormat != null) ? Plugins.GetFilesystemByFormat(HintFormat) : null;

                var Reader = Plugin?.GetFilesystemReader(Raw);
                if (Reader != null)
                {
                    var Dst = GetFs();
                    try { Reader.ReadToFs(Dst); } catch { }
                    if (Dst.Loaded)
                    {
                        Blob.Elements.Add(new LoadedElement(ImageKind.Filesystem, Plugin.Format, Dst.Info.Compression, Container, Plugin.Format));
                        return true;
                    }
                }
            }

            if (HintKind == ImageKind.Devtree)
            {
                var Dst = GetDtb();
                try { new Elements.Dtb.Reader.DtbReader(Raw).ReadToDevTree(Dst); } catch { }
                if (Dst.Loaded)
                {
                    Blob.Elements.Add(new LoadedElement(ImageKind.Devtree, HintFormat ?? "dtb", CompressionType.IH_COMP_NONE, Container, HintFormat ?? "dtb"));
                    return true;
                }
            }

            // 7) Unknown: preserve for round-trip.
            Blob.Unknown.Add(new UnknownImage(Container, Data.Offset, Data.Length));
            return false;
        }

        private Elements.Kernel.LinuxKernel GetKernel()
        {
            var Dst = Blob.GetKernel(0);
            if (Dst == null)
            {
                Dst = new Elements.Kernel.LinuxKernel();
                Blob.SetKernel(0, Dst);
            }
            return Dst;
        }

        private Elements.Fs.LinuxFilesystem GetFs()
        {
            var Dst = Blob.GetFilesystem(0);
            if (Dst == null)
            {
                Dst = new Elements.Fs.LinuxFilesystem();
                Blob.SetFilesystem(0, Dst);
            }
            return Dst;
        }

        private Elements.Dtb.DeviceTree GetDtb()
        {
            var Dst = Blob.GetDevTree(0);
            if (Dst == null)
            {
                Dst = new Elements.Dtb.DeviceTree();
                Blob.SetDevTree(0, Dst);
            }
            return Dst;
        }
    }
}
