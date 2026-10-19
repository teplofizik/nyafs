using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands.Fs
{
    public class Xattr : ScriptStepGenerator
    {
        public Xattr() : base("xattr")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.FsPathScriptArgsParam(),
                new Params.StringScriptArgsParam("name"),
                new Params.StringScriptArgsParam("value")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            return new XattrScriptStep(Args.RawArgs[0], Args.RawArgs[1], Args.RawArgs[2]);
        }

        public class XattrScriptStep : ScriptStep
        {
            string Path;
            string XAttrName;
            string Value;

            public XattrScriptStep(string Path, string Name, string Value) : base("xattr")
            {
                this.Path = Path;
                this.XAttrName = Name;
                this.Value = Value;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                if (!Fs.Exists(Path))
                    return new ScriptStepResult(ScriptStepStatus.Warning, $"{Path} not found!");

                var Item = Fs.GetElement(Path);

                if (Value == "-")
                {
                    Item.Xattrs.Remove(XAttrName);
                    return new ScriptStepResult(ScriptStepStatus.Ok, $"Removed xattr {XAttrName} of {Path}!");
                }

                byte[] Data;
                if (Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    var Hex = Value.Substring(2);
                    if ((Hex.Length % 2) != 0)
                        return new ScriptStepResult(ScriptStepStatus.Error, "Hex xattr value must have an even number of digits");

                    Data = new byte[Hex.Length / 2];
                    for (int i = 0; i < Data.Length; i++)
                        Data[i] = Convert.ToByte(Hex.Substring(i * 2, 2), 16);
                }
                else
                    Data = Encoding.UTF8.GetBytes(Value);

                Item.Xattrs[XAttrName] = Data;
                return new ScriptStepResult(ScriptStepStatus.Ok, $"Set xattr {XAttrName} ({Data.Length} bytes) of {Path}!");
            }
        }
    }
}
