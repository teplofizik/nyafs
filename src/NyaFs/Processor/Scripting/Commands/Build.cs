using System;
using System.IO;
using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Composite;
using NyaFs.Processor.Scripting.Helper;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// build {layout.yaml} {out} - build an image from a declarative layout in one step:
    /// create the plan, resolve the declared content (file/dir/blob) and write the image.
    /// The session workspace is not modified.
    /// </summary>
    public class Build : ScriptStepGenerator
    {
        public Build() : base("build")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[] {
                new Params.LocalPathScriptArgsParam(),
                new Params.LocalPathScriptArgsParam()
            }));
        }

        public override ScriptStep Get(ScriptArgs Args) =>
            new BuildStep(Args.RawArgs[0], Args.RawArgs[1]);

        public class BuildStep : ScriptStep
        {
            private readonly string Schema;
            private readonly string Output;

            public BuildStep(string Schema, string Output) : base("build")
            {
                this.Schema = Schema;
                this.Output = Output;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Filename = this.DetectFilePath(Schema);
                if (Filename == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Schema} not found!");

                var Document = PlanDocument.Load(Filename, out var Error);
                if (Document == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot parse layout: {Error}");

                if (String.IsNullOrEmpty(Document.Format))
                    return new ScriptStepResult(ScriptStepStatus.Error, "Layout has no format");

                var Plugin = Processor.Plugins.ResolveImage(Document.Format);
                if (Plugin == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown container format '{Document.Format}'");

                if (!Plugin.SupportLayout)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Format '{Plugin.Format}' does not support layout");

                var Root = Plugin.CreatePlan(Document, out Error);
                if (Root == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot create layout: {Error}");

                PlanDocument.SetSource(Root, Filename);

                var Blob = new BaseImageBlob { BuildPlan = new ImageBuildPlan(Root) };
                var Data = Plugin.Build(Blob, Processor.Plugins, out Error);
                if (Data == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot build image: {Error}");

                try
                {
                    System.IO.File.WriteAllBytes(Output, Data);
                }
                catch (Exception E)
                {
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot write {Output}: {E.Message}");
                }

                return new ScriptStepResult(ScriptStepStatus.Ok,
                    $"Image is built to {Output} ({Data.Length} bytes).");
            }
        }
    }
}
