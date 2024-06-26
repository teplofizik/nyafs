﻿using System;
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
                new string[] { "cpio", "gz", "gzip", "lzma", "lz4", "bz2", "bzip2", "legacy", "ext2", "squashfs", "cramfs", "romfs" }));

            AddConfig(new Configs.ImageScriptArgsConfig(2, "devtree",
                new string[] { "dtb" }));

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
                switch (Format)
                {
                    case "raw":
                        {
                            var Kernel = Processor.GetKernel();
                            if ((Kernel != null) && Kernel.Loaded)
                            {
                                ImageFormat.Helper.LogHelper.KernelInfo(Kernel);
                                var Exporter = new NyaFs.ImageFormat.Elements.Kernel.Writer.ArchiveWriter(Path, ImageFormat.Types.CompressionType.IH_COMP_NONE);
                                Exporter.WriteKernel(Kernel);
                                return new ScriptStepResult(ScriptStepStatus.Ok, $"Kernel is stored to file {Path} as raw image!");
                            }
                            else
                                return new ScriptStepResult(ScriptStepStatus.Error, $"Kernel is not loaded!");
                        }
                    case "lz4":
                    case "lzma":
                    case "gz":
                    case "gzip":
                    case "bz2":
                    case "bzip2":
                        {
                            var Kernel = Processor.GetKernel();
                            if ((Kernel != null) && Kernel.Loaded)
                            {
                                var CompressionType = Helper.ArchiveHelper.GetCompressionFormat(Format);

                                ImageFormat.Helper.LogHelper.KernelInfo(Kernel);
                                var Exporter = new NyaFs.ImageFormat.Elements.Kernel.Writer.ArchiveWriter(Path, CompressionType);
                                Exporter.WriteKernel(Kernel);
                                return new ScriptStepResult(ScriptStepStatus.Ok, $"Kernel is stored to file {Path} as {Format} compressed image!");
                            }
                            else
                                return new ScriptStepResult(ScriptStepStatus.Error, $"Kernel is not loaded!");
                        }
                    case "legacy":
                        {
                            var Kernel = Processor.GetKernel();
                            if ((Kernel != null) && Kernel.Loaded)
                            {
                                ImageFormat.Helper.LogHelper.KernelInfo(Kernel);
                                var Exporter = new ImageFormat.Elements.Kernel.Writer.LegacyWriter(Path);
                                Exporter.WriteKernel(Kernel);
                                return new ScriptStepResult(ScriptStepStatus.Ok, $"Kernel is stored to file {Path} as legacy image!");
                            }
                            else
                                return new ScriptStepResult(ScriptStepStatus.Error, $"Kernel is not loaded!");
                        }
                    default:
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown kernel image format!");
                }
            }

            private ScriptStepResult StoreDtb(ImageProcessor Processor)
            {
                switch (Format)
                {
                    case "dtb":
                        {
                            var Dtb = Processor.GetDevTree();
                            if (Dtb != null)
                            {
                                ImageFormat.Helper.LogHelper.DevtreeInfo(Dtb);

                                var data = new NyaFs.FlattenedDeviceTree.Writer.FDTWriter(Dtb.DevTree);
                                System.IO.File.WriteAllBytes(Path, data.GetBinary());

                                return new ScriptStepResult(ScriptStepStatus.Ok, $"Dtb is stored to file {Path}!");
                            }
                            else
                                return new ScriptStepResult(ScriptStepStatus.Error, $"Device tree is not loaded!");
                        }
                    case "dts":
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Dts is not supported now!");
                    default:
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown devtree format!");
                }
            }


            private ImageFormat.Elements.Fs.Writer.Writer GetFsWriter(ImageFormat.Elements.Fs.LinuxFilesystem Fs)
            {
                switch (Format)
                {
                    case "legacy": return new ImageFormat.Elements.Fs.Writer.LegacyWriter(Path);
                    case "cpio": return new ImageFormat.Elements.Fs.Writer.CpioFsWriter(Path);
                    case "ext2": return new ImageFormat.Elements.Fs.Writer.Ext2FsWriter(ImageFormat.Elements.Fs.Writer.Writer.DetectFixDiskSize(Fs, 0x800000), Path);
                    case "squashfs": return new ImageFormat.Elements.Fs.Writer.SquashFsWriter(Fs.SquashFsCompression, Path);
                    case "cramfs": return new ImageFormat.Elements.Fs.Writer.CramFsWriter(Path);
                    case "romfs": return new ImageFormat.Elements.Fs.Writer.RomFsWriter(Path);
                    case "lz4":
                    case "lzma":
                    case "gz":
                    case "gzip":
                    case "bz2":
                    case "bzip2":
                        var CompressionType = Helper.ArchiveHelper.GetCompressionFormat(Format);
                        return new ImageFormat.Elements.Fs.Writer.ArchiveCpioWriter(Path, CompressionType);
                    default:
                        return null;
                }
            }

            private ScriptStepResult StoreFs(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if((Fs == null) || (Fs.Loaded == false))
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Filesystem is not loaded!");

                var Writer = GetFsWriter(Fs);
                if(Writer != null)
                {
                    if (Writer.CheckFilesystem(Fs))
                    {
                        Writer.WriteFs(Fs);
                        return new ScriptStepResult(ScriptStepStatus.Ok, $"Filesystem is stored to file {Path} as {Format} image!");
                    }
                    else
                        return new ScriptStepResult(ScriptStepStatus.Ok, $"Cannot store as {Format} image: no enough info about target system!");
                }
                else
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown fs format!");
            }
        }
    }
}
