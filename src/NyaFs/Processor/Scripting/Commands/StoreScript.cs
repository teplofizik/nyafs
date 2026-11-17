using NyaFs.ImageFormat.Composite;
using NyaFs.Processor;
using NyaFs.Processor.Scripting;
using NyaFs.Processor.Scripting.Helper;
using Params = NyaFs.Processor.Scripting.Params;
using System;
using System.IO;

namespace NyaFs.Processor.Scripting.Commands
{
    public class StoreScript : ScriptStepGenerator
    {
        public StoreScript() : base("storescript")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.FsPathScriptArgsParam(),
                new Params.LocalPathScriptArgsParam()
            }));
        }

        public override ScriptStep Get(ScriptArgs Args) =>
            new StoreScriptStep(Args.RawArgs[0], Args.RawArgs[1]);

        public class StoreScriptStep : ScriptStep
        {
            private readonly string Output;
            private readonly string ScriptPath;

            public StoreScriptStep(string Output, string ScriptPath) : base("storescript")
            {
                this.Output = Output;
                this.ScriptPath = ScriptPath;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Filename = this.DetectFilePath(ScriptPath);
                if (Filename == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{ScriptPath} not found");

                try
                {
                    var Data = File.ReadAllBytes(Filename);
                    var Image = LegacyMultiImageWriter.BuildScript(Data,
                        Path.GetFileNameWithoutExtension(Filename));
                    File.WriteAllBytes(Output, Image);
                    return new ScriptStepResult(ScriptStepStatus.Ok, $"U-Boot script written to {Output}");
                }
                catch (Exception E)
                {
                    return new ScriptStepResult(ScriptStepStatus.Error, E.Message);
                }
            }
        }
    }
}
