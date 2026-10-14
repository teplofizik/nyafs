using NyaFs.ImageFormat;
using NyaFs.Processor.Scripting.Helper;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// inspect {localfile} - recursively list all discoverable sub-images of a file with
    /// their stable paths (usable later to load a specific sub-image).
    /// </summary>
    public class Inspect : ScriptStepGenerator
    {
        public Inspect() : base("inspect")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[] {
                new Params.LocalPathScriptArgsParam()
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            return new InspectScriptStep(Args.RawArgs[0]);
        }

        public class InspectScriptStep : ScriptStep
        {
            private string Path;

            public InspectScriptStep(string Path) : base("inspect")
            {
                this.Path = Path;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Filename = this.DetectFilePath(Path);
                if (Filename == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Path} not found!");

                var Data = System.IO.File.ReadAllBytes(Filename);
                var Inspector = new ImageInspector(Processor.Plugins);
                var Root = Inspector.Inspect(System.IO.Path.GetFileName(Filename), Data);

                Dump(Root, 0);

                return new ScriptStepResult(ScriptStepStatus.Ok, "Image inspected.");
            }

            private void Dump(ImageNode Node, int Indent)
            {
                var Pad = new string(' ', Indent * 2);
                var Label = (Node.Path.Length == 0) ? $"{Node.Name} (root)" : Node.Path;

                var Info = $"{Node.Kind}/{Node.Format ?? "?"}";
                if (Node.Compression != ImageFormat.Types.CompressionType.IH_COMP_NONE)
                    Info += $" comp={Node.Compression}";

                Log.Write(0, $"{Pad}{Label}  [{Info}] {Node.Size} bytes");

                foreach (var Child in Node.Children)
                    Dump(Child, Indent + 1);
            }
        }
    }
}
