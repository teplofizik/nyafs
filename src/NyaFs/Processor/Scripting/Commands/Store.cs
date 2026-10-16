using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands
{
    public class Store : ScriptStepGenerator
    {
        public Store() : base("store")
        {
            AddConfig(new Configs.ImageScriptArgsConfig(0, "kernel",
                new string[] { "raw", "gz", "gzip", "lzma", "lz4", "bz2", "bzip2", "legacy" }));

            AddConfig(new Configs.ImageScriptArgsConfig(1, "ramfs",
                new string[] { "cpio", "tar", "gz", "gzip", "lzma", "lz4", "bz2", "bzip2", "xz", "zstd", "legacy", "ext2", "squashfs", "cramfs", "romfs",
                    "littlefs", "spiffs", "tar.gz", "tar.xz", "tar.bz2", "tar.zstd", "tar.lz4", "tar.lzma" }));

            AddConfig(new Configs.ImageScriptArgsConfig(2, "devtree",
                new string[] { "dtb", "dts" }));

            AddConfig(new ScriptArgsConfig(3, new ScriptArgsParam[] {
                    new Params.FsPathScriptArgsParam()
                }));

            AddConfig(new Configs.ErrorConfig("Invalid image type: %1%. Must be one of: kernel, ramfs, devtree"));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            var A = Args.RawArgs;

            if(Args.ArgConfig == 3)
                return new StoreScriptStep(A[0], "all", "fit");
            else
                return new StoreScriptStep(A[0], A[1], A[2]);
        }

        public class StoreScriptStep : ScriptStep
        {
            string Path;
            string Type;
            string Format;

            public StoreScriptStep(string Path, string Type, string Format) : base("store")
            {
                this.Path = Path;
                this.Type = Type;
                this.Format = Format;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                switch (Type)
                {
                    case "ramfs": return StoreFs(Processor);
                    case "devtree": return StoreDtb(Processor);
                    case "kernel": return StoreKernel(Processor);
                    case "all": return StoreAll(Processor);
                    default:
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown image type!");
                }
            }

            private ScriptStepResult StoreAll(ImageProcessor Processor)
            {
                switch (Format)
                {
                    case "fit":
                        {
                            var Kernel = Processor.GetKernel();
                            var Fs = Processor.GetFs();
                            var Dtb = Processor.GetDevTree();
                            if (((Kernel == null) || !Kernel.Loaded) &&
                                ((Fs == null) || (Fs.Loaded == false)) &&
                                ((Dtb == null) || !Dtb.Loaded))
                                return new ScriptStepResult(ScriptStepStatus.Error, $"No images are loaded!");

                            if ((Kernel != null) && Kernel.Loaded)
                                ImageFormat.Helper.LogHelper.KernelInfo(Kernel);

                            if ((Fs != null) && Fs.Loaded)
                                ImageFormat.Helper.LogHelper.RamfsInfo(Fs);

                            if ((Dtb != null) && Dtb.Loaded)
                                ImageFormat.Helper.LogHelper.DevtreeInfo(Dtb);

                            var Writer = new ImageFormat.Composite.FitWriter(Path);
                            if(Writer.Write(Processor.GetBlob()))
                                return new ScriptStepResult(ScriptStepStatus.Ok, $"Images are stored to file {Path} as FIT Image!");
                            else
                                return new ScriptStepResult(ScriptStepStatus.Ok, $"Cannot compile FIT Image! No enough information...");
                        }
                    default:
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown multiimage format!");

                }
            }

            private ScriptStepResult StoreKernel(ImageProcessor Processor)
            {
                var Kernel = Processor.GetKernel();
                if ((Kernel == null) || !Kernel.Loaded)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Kernel is not loaded!");

                ImageFormat.Helper.LogHelper.KernelInfo(Kernel);

                if (Format == "legacy")
                {
                    var Exporter = new ImageFormat.Elements.Kernel.Writer.LegacyWriter(Path);
                    Exporter.WriteKernel(Kernel);
                    return new ScriptStepResult(ScriptStepStatus.Ok, $"Kernel is stored to file {Path} as legacy image!");
                }

                // Raw kernel bytes are provided by the kernel plugins (no hardcoded writer here).
                var Plugin = Processor.Plugins.GetKernelByFormat("raw");
                var Writer = ((Plugin != null) && Plugin.SupportWrite) ? Plugin.GetKernelWriter(Kernel) : null;
                if (Writer == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown kernel format '{Format}'!");

                Writer.WriteKernel(Kernel);
                if (!Writer.HasRawStreamData)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot build kernel image!");

                if (Format == "raw")
                {
                    System.IO.File.WriteAllBytes(Path, Writer.RawStream);
                    return new ScriptStepResult(ScriptStepStatus.Ok, $"Kernel is stored to file {Path} as raw image!");
                }

                switch (Format)
                {
                    case "lz4":
                    case "lzma":
                    case "gz":
                    case "gzip":
                    case "bz2":
                    case "bzip2":
                    case "xz":
                    case "zstd":
                        {
                            var CompressionType = Helper.ArchiveHelper.GetCompressionFormat(Format);
                            System.IO.File.WriteAllBytes(Path, ImageFormat.Helper.FitHelper.GetCompressedData(Writer.RawStream, CompressionType));
                            return new ScriptStepResult(ScriptStepStatus.Ok, $"Kernel is stored to file {Path} as {Format} compressed image!");
                        }
                    default:
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown kernel format '{Format}'!");
                }
            }

            private ScriptStepResult StoreDtb(ImageProcessor Processor)
            {
                var Dtb = Processor.GetDevTree();
                if (Dtb == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Device tree is not loaded!");

                var Plugin = Processor.Plugins.GetDtbByFormat(Format);
                if ((Plugin == null) || !Plugin.SupportWrite)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown devtree format '{Format}'!");

                ImageFormat.Helper.LogHelper.DevtreeInfo(Dtb);

                var Writer = Plugin.GetDevtreeWriter(Dtb);
                if (Writer == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown devtree format '{Format}'!");

                Writer.WriteDevtree(Dtb);
                if (Writer.HasRawStreamData)
                    System.IO.File.WriteAllBytes(Path, Writer.RawStream);

                return new ScriptStepResult(ScriptStepStatus.Ok, $"Dtb is stored to file {Path}!");
            }


            /// <summary>
            /// Formats the built-in writers handle plus the writable filesystem plugins.
            /// Used only to make the "unknown format" error helpful.
            /// </summary>
            private static string AvailableFsFormats(ImageProcessor Processor)
            {
                var Res = new List<string> {
                    "cpio", "tar", "gz", "gzip", "lzma", "lz4", "bz2", "bzip2", "xz", "zstd",
                    "legacy", "ext2", "squashfs", "cramfs", "romfs",
                    "tar.gz", "tar.xz", "tar.bz2", "tar.zstd", "tar.lz4", "tar.lzma"
                };

                foreach (var P in Processor.Plugins.GetFilesystems())
                    if (P.SupportWrite && !Res.Contains(P.Format))
                        Res.Add(P.Format);

                return string.Join(", ", Res);
            }

            private ImageFormat.Elements.Fs.Writer.Writer GetFsWriter(ImageProcessor Processor, ImageFormat.Elements.Fs.LinuxFilesystem Fs)
            {
                switch (Format)
                {
                    case "legacy": return new ImageFormat.Elements.Fs.Writer.LegacyWriter(Path);
                    case "cpio": return new ImageFormat.Elements.Fs.Writer.CpioFsWriter(Path);
                    case "ext2": return new ImageFormat.Elements.Fs.Writer.Ext2FsWriter(ImageFormat.Elements.Fs.Writer.Writer.DetectFixDiskSize(Fs, 0x800000), Path);
                    case "squashfs": return new ImageFormat.Elements.Fs.Writer.SquashFsWriter(Fs.SquashFsCompression, Path);
                    case "cramfs": return new ImageFormat.Elements.Fs.Writer.CramFsWriter(Path);
                    case "romfs": return new ImageFormat.Elements.Fs.Writer.RomFsWriter(Path);
                    case "tar": return new ImageFormat.Elements.Fs.Writer.TarFsWriter(Path);
                    case "tar.gz":
                    case "tar.xz":
                    case "tar.bz2":
                    case "tar.zstd":
                    case "tar.lz4":
                    case "tar.lzma":
                        var TarCompressionType = Helper.ArchiveHelper.GetCompressionFormat(Format.Substring(4));
                        return new ImageFormat.Elements.Fs.Writer.ArchiveTarWriter(Path, TarCompressionType);
                    case "lz4":
                    case "lzma":
                    case "gz":
                    case "gzip":
                    case "bz2":
                    case "bzip2":
                    case "xz":
                    case "zstd":
                        var CompressionType = Helper.ArchiveHelper.GetCompressionFormat(Format);
                        return new ImageFormat.Elements.Fs.Writer.ArchiveCpioWriter(Path, CompressionType);
                    default:
                        // Formats provided by filesystem plugins (e.g. littlefs, spiffs).
                        foreach (var Plugin in Processor.Plugins.GetFilesystems())
                        {
                            if ((Plugin.Format == Format) && Plugin.SupportWrite)
                                return Plugin.GetFilesystemWriter(Fs);
                        }
                        return null;
                }
            }

            private ScriptStepResult StoreFs(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Filesystem is not loaded!");

                var Writer = GetFsWriter(Processor, Fs);
                if(Writer != null)
                {
                    if (Writer.CheckFilesystem(Fs))
                    {
                        Writer.WriteFs(Fs);

                        // Built-in writers take the file name and write it themselves; plugin
                        // writers (e.g. littlefs/spiffs) only expose the built bytes as RawStream.
                        if (Writer.HasRawStreamData)
                            System.IO.File.WriteAllBytes(Path, Writer.RawStream);

                        return new ScriptStepResult(ScriptStepStatus.Ok, $"Filesystem is stored to file {Path} as {Format} image!");
                    }
                    else
                        return new ScriptStepResult(ScriptStepStatus.Ok, $"Cannot store as {Format} image: no enough info about target system!");
                }
                else
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown fs format '{Format}'. Available: {AvailableFsFormats(Processor)}");
            }
        }
    }
}
