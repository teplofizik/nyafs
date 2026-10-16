using NyaFs.ImageFormat;
using NyaFs.Processor.Scripting.Steps;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands
{
    public class Load : ScriptStepGenerator
    {
        public Load() : base("load")
        {
            // The format lists below are only hints for autocompletion/error messages; the
            // actual readers come from the plugins (see ImagePipeline). New formats added as
            // plugins work without touching this command.
            AddConfig(new Configs.ImageScriptArgsConfig(0, "kernel",
                new string[] { "raw", "gz", "gzip", "lzma", "lz4", "bz2", "lzo", "zstd", "bzip2", "legacy", "fit", "android", "zimage", "cimg" }));

            AddConfig(new Configs.ImageScriptArgsConfig(1, "ramfs",
                new string[] { "cpio", "gz", "gzip", "lzma", "lz4", "bz2", "lzo", "zstd", "bzip2", "fit", "android", "legacy", "ext2", "squashfs", "cramfs", "romfs", "cimg" }));

            AddConfig(new Configs.ImageScriptArgsConfig(2, "devtree",
                new string[] { "dtb", "gz", "gzip", "lzma", "lz4", "bz2", "lzo", "zstd", "bzip2", "fit" }));

            AddConfig(new ScriptArgsConfig(3, new ScriptArgsParam[] { new Params.FsPathScriptArgsParam() }));

            AddConfig(new ScriptArgsConfig(4, new ScriptArgsParam[] { new Params.FsPathScriptArgsParam(), new Params.StringScriptArgsParam("config") }));

            AddConfig(new Configs.ErrorConfig("Invalid image type: %1%. Must be one of: kernel, ramfs, devtree"));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            var A = Args.RawArgs;

            if (Args.ArgConfig == 3)
                return new LoadScriptStep(A[0], "detect", null);
            else if (Args.ArgConfig == 4)
                return new LoadScriptStep(A[0], "detect", A[1]);
            else
                return new LoadScriptStep(A[0], A[1], A[2]);
        }

        public class LoadScriptStep : ImageImportScriptStep
        {
            string Path;
            string Type;
            string Format;

            public LoadScriptStep(string Path, string Type, string Format) : base("load")
            {
                this.Path = Path;
                this.Type = Type;
                this.Format = Format;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                if (!System.IO.File.Exists(Path))
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Path} not found!");

                var Data = System.IO.File.ReadAllBytes(Path);

                ImageKind Kind;
                string Hint = null;

                switch (Type)
                {
                    case "detect": Kind = ImageKind.Unknown; break;
                    case "kernel": Kind = ImageKind.Kernel; Hint = Format; break;
                    case "ramfs": Kind = ImageKind.Filesystem; Hint = Format; break;
                    case "devtree": Kind = ImageKind.Devtree; Hint = Format; break;
                    case "all": Kind = ImageKind.Unknown; Hint = Format; break;
                    default:
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown image type!");
                }

                // Everything is loaded through the plugin-driven pipeline; the type/format
                // only act as a hint when content detection cannot decide.
                var Pipeline = new ImagePipeline(Processor.Plugins, Processor.GetBlob());

                var Loaded = (Type == "detect") ? Pipeline.Load(Data) : Pipeline.Load(Data, Kind, Hint);

                if (Loaded)
                    return new ScriptStepResult(ScriptStepStatus.Ok, "Images are loaded (auto-detected).");
                else
                    return new ScriptStepResult(ScriptStepStatus.Error, "Cannot detect or load image!");
            }
        }
    }
}
