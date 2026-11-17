using NyaFs.Processor.Scripting;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands.Fs
{
    public class Chgrp : ScriptStepGenerator
    {
        public Chgrp() : base("chgrp")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.FsPathScriptArgsParam(),
                new Params.StringScriptArgsParam("group")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args) =>
            new ChgrpScriptStep(Args.RawArgs[0], Args.RawArgs[1]);

        public class ChgrpScriptStep : ScriptStep
        {
            string Path;
            string Group;

            public ChgrpScriptStep(string Path, string Group) : base("chgrp")
            {
                this.Path = Path;
                this.Group = Group;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                if (!Fs.Exists(Path))
                    return new ScriptStepResult(ScriptStepStatus.Warning, $"{Path} not found!");

                uint Gid;
                string Error;
                if (!Chown.TryResolveGroup(Fs, Group, out Gid, out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);

                var Item = Fs.GetElement(Path);
                Item.Group = Gid;

                return new ScriptStepResult(ScriptStepStatus.Ok, $"Group of {Path} is updated to {Group} (gid {Gid})!");
            }
        }
    }
}
