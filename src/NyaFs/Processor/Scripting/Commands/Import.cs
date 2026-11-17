using NyaFs.ImageFormat;
using NyaFs.Processor.Scripting.Helper;
using System;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// Replace one element of the active build plan from a part of another image.
    /// The workspace itself is not reset.
    /// </summary>
    public class Import : ScriptStepGenerator
    {
        public Import() : base("import")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.LocalPathScriptArgsParam(),
                new Params.StringScriptArgsParam("source-path")
            }));
            AddConfig(new ScriptArgsConfig(1, new ScriptArgsParam[]
            {
                new Params.LocalPathScriptArgsParam(),
                new Params.StringScriptArgsParam("source-path"),
                new Params.StringScriptArgsParam("target-path")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args) =>
            new ImportScriptStep(Args.RawArgs[0], Args.RawArgs[1],
                (Args.ArgConfig == 1) ? Args.RawArgs[2] : null);

        public class ImportScriptStep : ScriptStep
        {
            private readonly string Source;
            private readonly string SourcePath;
            private readonly string TargetPath;

            public ImportScriptStep(string Source, string SourcePath, string TargetPath) : base("import")
            {
                this.Source = Source;
                this.SourcePath = SourcePath;
                this.TargetPath = TargetPath;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Filename = this.DetectFilePath(Source);
                if (Filename == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Source} not found!");

                var Plan = Processor.GetBlob().BuildPlan;
                if (Plan == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "No build plan is loaded");

                var Target = (TargetPath != null) ? Plan.Find(TargetPath) : Processor.ActiveNode;
                if (Target == null)
                    return new ScriptStepResult(ScriptStepStatus.Error,
                        (TargetPath != null) ? $"Target node '{TargetPath}' is not found" : "No active element; use <path> first");
                if ((Target.Fs == null) && (Target.Kernel == null) && (Target.Dtb == null) &&
                    ((Target.Raw == null) || (Target.Children.Count > 0)))
                    return new ScriptStepResult(ScriptStepStatus.Error, $"'{Target.Path}' is a container, not an editable element");

                var Data = System.IO.File.ReadAllBytes(Filename);
                var Inspector = new ImageInspector(Processor.Plugins);
                var Located = Inspector.Locate(Data, SourcePath);
                if (Located == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Sub-image '{SourcePath}' is not found in {Source}");

                var Temp = (Target.Kind == ImageKind.Unknown) ? null : new BaseImageBlob();
                if (Temp != null)
                {
                    var Pipeline = new ImagePipeline(Processor.Plugins, Temp);
                    if (!Pipeline.LoadInto(Located.Content, Located.Kind, Located.Format, Located.Info))
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot load sub-image '{SourcePath}'");
                }

                Target.Raw = Located.Content;
                Target.Format = Located.Format;
                if ((Located.Format == "fit") && (Target.Kind == ImageKind.Devtree))
                    Target.Format = "dtb";
                else if ((Located.Format == "fit") && (Target.Kind != ImageKind.Unknown))
                    Target.Format = "raw";
                var DetectedCompression = NyaFs.ImageFormat.Helper.FitHelper.DetectCompression(Located.Content);
                Target.Compression = (DetectedCompression != NyaFs.ImageFormat.Types.CompressionType.IH_COMP_NONE)
                    ? DetectedCompression
                    : ((Located.Info != null) && (Located.Info.Compression != NyaFs.ImageFormat.Types.CompressionType.IH_COMP_NONE))
                        ? Located.Info.Compression
                        : Located.Compression;
                if (Located.Info != null)
                    Target.Info = Located.Info;

                switch (Target.Kind)
                {
                    case ImageKind.Filesystem:
                        Target.Fs = Temp.GetFilesystem(0);
                        if (Target.Fs == null)
                            return new ScriptStepResult(ScriptStepStatus.Error, $"Sub-image '{SourcePath}' is not a filesystem");
                        Processor.GetBlob().SetFilesystem(0, Target.Fs);
                        break;
                    case ImageKind.Kernel:
                        Target.Kernel = Temp.GetKernel(0);
                        if (Target.Kernel == null)
                            return new ScriptStepResult(ScriptStepStatus.Error, $"Sub-image '{SourcePath}' is not a kernel");
                        Processor.GetBlob().SetKernel(0, Target.Kernel);
                        break;
                    case ImageKind.Devtree:
                        Target.Dtb = Temp.GetDevTree(0);
                        if (Target.Dtb == null)
                            return new ScriptStepResult(ScriptStepStatus.Error, $"Sub-image '{SourcePath}' is not a device tree");
                        Processor.GetBlob().SetDevTree(0, Target.Dtb);
                        break;
                    case ImageKind.Unknown:
                        break;
                    default:
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot import into '{Target.Kind}'");
                }

                Plan.MarkDirty(Target.Path);
                Processor.ActiveNode = Target;
                return new ScriptStepResult(ScriptStepStatus.Ok, $"Imported {Located.Kind}/{Located.Format} into {Target.Path}");
            }
        }
    }
}
