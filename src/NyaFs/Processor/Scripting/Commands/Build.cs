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

                PlanDocument.SetDocumentSource(Document.Options, Filename);

                var Plugin = Processor.Plugins.ResolveImage(Document.Format);
                if (Plugin == null)
                {
                    var FsPlugin = Processor.Plugins.ResolveFilesystem(Document.Format);
                    if (FsPlugin == null)
                        return new ScriptStepResult(ScriptStepStatus.Error,
                            $"Unknown container or filesystem format '{Document.Format}'");

                    var FsData = FilesystemImageBuilder.Build(Document, Processor.Plugins, out Error);
                    if (FsData == null)
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot build image: {Error}");

                    return Write(Output, FsData);
                }

                if (!Plugin.SupportLayout)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Format '{Plugin.Format}' does not support layout");

                var Root = Plugin.CreatePlan(Document, out Error);
                if (Root == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot create layout: {Error}");

                PlanDocument.SetSource(Root, Filename);

                var Blob = new BaseImageBlob { BuildPlan = new ImageBuildPlan(Root) };

                if (Plugin.SupportsStreaming)
                {
                    try
                    {
                        using (var Stream = System.IO.File.Create(Output))
                        {
                            if (!Plugin.BuildTo(Stream, Blob, Processor.Plugins, out Error))
                            {
                                Stream.Close();
                                System.IO.File.Delete(Output);
                                return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot build image: {Error}");
                            }
                        }
                    }
                    catch (Exception E)
                    {
                        try { System.IO.File.Delete(Output); } catch { }
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot write {Output}: {E.Message}");
                    }

                    var Size = new System.IO.FileInfo(Output).Length;
                    return new ScriptStepResult(ScriptStepStatus.Ok,
                        $"Image is built to {Output} ({Size} bytes).");
                }

                var Data = Plugin.Build(Blob, Processor.Plugins, out Error);
                if (Data == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot build image: {Error}");

                return Write(Output, Data);
            }

            private static ScriptStepResult Write(string Output, byte[] Data)
            {
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
