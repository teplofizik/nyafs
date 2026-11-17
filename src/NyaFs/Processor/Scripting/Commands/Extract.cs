using NyaFs.ImageFormat;
using NyaFs.Processor.Scripting.Helper;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// extract {localfile} {subpath} {outfile} - write a sub-image (by its stable path from
    /// 'inspect') to a file byte for byte, without further interpretation. Empty subpath
    /// (or ".") writes the whole file.
    /// </summary>
    public class Extract : ScriptStepGenerator
    {
        public Extract() : base("extract")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[] {
                new Params.LocalPathScriptArgsParam(),
                new Params.StringScriptArgsParam("subpath"),
                new Params.StringScriptArgsParam("outfile")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            return new ExtractScriptStep(Args.RawArgs[0], Args.RawArgs[1], Args.RawArgs[2]);
        }

        public class ExtractScriptStep : ScriptStep
        {
            private string Path;
            private string SubPath;
            private string OutPath;

            public ExtractScriptStep(string Path, string SubPath, string OutPath) : base("extract")
            {
                this.Path = Path;
                this.SubPath = SubPath;
                this.OutPath = OutPath;
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

                try
                {
                    System.IO.File.WriteAllBytes(OutPath, Located.Content);
                }
                catch (Exception E)
                {
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot write {OutPath}: {E.Message}");
                }

                return new ScriptStepResult(ScriptStepStatus.Ok, $"Sub-image '{SubPath}' ({Located.Content.Length} bytes) is extracted to {OutPath}.");
            }
        }
    }
}
