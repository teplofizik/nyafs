using NyaFs.ImageFormat;
using NyaFs.Processor.Scripting.Helper;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// load.sub {localfile} {subpath} - load a single sub-image by its stable path
    /// (as listed by 'inspect'). Empty subpath (or ".") loads the whole image.
    /// A separate command (instead of '#') because FIT configuration names contain '#'.
    /// </summary>
    public class LoadSub : ScriptStepGenerator
    {
        public LoadSub() : base("load.sub")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[] {
                new Params.LocalPathScriptArgsParam(),
                new Params.StringScriptArgsParam("subpath")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            return new LoadSubScriptStep(Args.RawArgs[0], Args.RawArgs[1]);
        }

        public class LoadSubScriptStep : ScriptStep
        {
            private string Path;
            private string SubPath;

            public LoadSubScriptStep(string Path, string SubPath) : base("load.sub")
            {
                this.Path = Path;
                this.SubPath = SubPath;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Filename = this.DetectFilePath(Path);
                if (Filename == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Path} not found!");

                var Data = System.IO.File.ReadAllBytes(Filename);

                var Inspector = new ImageInspector(Processor.Plugins);
                var Located = Inspector.Locate(Data, SubPath);
                if (Located == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Sub-image '{SubPath}' is not found in {Path}!");

                var Pipeline = new ImagePipeline(Processor.Plugins, Processor.GetBlob());
                if (Pipeline.Load(Located.Content, Located.Kind, Located.Format))
                    return new ScriptStepResult(ScriptStepStatus.Ok, $"Sub-image '{SubPath}' is loaded.");
                else
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot load sub-image '{SubPath}'.");
            }
        }
    }
}
