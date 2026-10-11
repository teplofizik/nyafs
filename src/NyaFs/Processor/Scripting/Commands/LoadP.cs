using NyaFs.Processor.Scripting.Importer;
using NyaFs.Processor.Scripting.Steps;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands
{
    internal class LoadP : ScriptStepGenerator
    {
        public LoadP() : base("loadp")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[] { new Params.FsPathScriptArgsParam() }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            var A = Args.RawArgs;

            return new LoadScriptStep(A[0]);
        }

        public class LoadScriptStep : ImageImportScriptStep
        {
            string Path;

            public LoadScriptStep(string Path) : base("loadp")
            {
                this.Path = Path;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                // Проверим наличие локального файла, содержимое которого надо загрузить в ФС
                if (!System.IO.File.Exists(Path))
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Path} not found!");

                byte[] Raw = System.IO.File.ReadAllBytes(Path);

                var Importer = new ImageImporter();
                if (Importer.Import(Processor, Raw))
                    return new ScriptStepResult(ScriptStepStatus.Ok, $"Image is loaded!");

                return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown image type!");
            }
        }
    }
}
