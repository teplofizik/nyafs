using System;
using System.Linq;
using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Composite;
using NyaFs.Processor.Scripting.Helper;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// mkimage {format} {layout.yaml} - create an empty build plan for a composite image
    /// from a declarative layout (YAML; JSON is accepted). The plan can then be filled with
    /// `use`/`import`/`mkfs`/`loaddir`/`file` and written with `store`.
    /// </summary>
    public class MkImage : ScriptStepGenerator
    {
        public MkImage() : base("mkimage")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[] {
                new Params.StringScriptArgsParam("format"),
                new Params.LocalPathScriptArgsParam()
            }));
        }

        public override ScriptStep Get(ScriptArgs Args) =>
            new MkImageStep(Args.RawArgs[0], Args.RawArgs[1]);

        public class MkImageStep : ScriptStep
        {
            private readonly string Format;
            private readonly string Schema;

            public MkImageStep(string Format, string Schema) : base("mkimage")
            {
                this.Format = Format;
                this.Schema = Schema;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Filename = this.DetectFilePath(Schema);
                if (Filename == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Schema} not found!");

                var Document = PlanDocument.Load(Filename, out var Error);
                if (Document == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot parse layout: {Error}");

                var Name = String.IsNullOrEmpty(Format) ? Document.Format : Format;
                if (String.IsNullOrEmpty(Name))
                    return new ScriptStepResult(ScriptStepStatus.Error, "Layout has no format and no format argument is given");

                var Plugin = Processor.Plugins.ResolveImage(Name);
                if (Plugin == null)
                {
                    var Formats = String.Join(", ", Processor.Plugins.GetImages().Where(P => P.SupportLayout).Select(P => P.Format));
                    return new ScriptStepResult(ScriptStepStatus.Error,
                        $"Unknown container format '{Name}'. Layout-capable formats: {Formats}");
                }

                if (!Plugin.SupportLayout)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Format '{Plugin.Format}' does not support layout");

                var Root = Plugin.CreatePlan(Document, out Error);
                if (Root == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot create layout: {Error}");

                PlanDocument.SetSource(Root, Filename);

                Processor.Reset();
                Processor.GetBlob().BuildPlan = new ImageBuildPlan(Root);

                return new ScriptStepResult(ScriptStepStatus.Ok,
                    $"Build plan '{Root.Format}' is created ({Root.Children.Count} slot(s)).");
            }
        }
    }
}
