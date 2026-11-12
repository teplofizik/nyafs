using NyaFs.Processor.Scripting;
using System;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>Select an element of the loaded build plan for editing.</summary>
    public class Use : ScriptStepGenerator
    {
        public Use() : base("use")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.StringScriptArgsParam("path")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args) => new UseScriptStep(Args.RawArgs[0]);

        public class UseScriptStep : ScriptStep
        {
            private readonly string Path;

            public UseScriptStep(string Path) : base("use")
            {
                this.Path = Path;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Plan = Processor.GetBlob().BuildPlan;
                if (Plan == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "No build plan is loaded");

                var Node = Plan.Find(Path);
                if (Node == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Build plan node '{Path}' is not found");

                if ((Node.Fs == null) && (Node.Kernel == null) && (Node.Dtb == null) &&
                    ((Node.Raw == null) || (Node.Children.Count > 0)))
                    return new ScriptStepResult(ScriptStepStatus.Error, $"'{Path}' is a container, not an editable element");

                Processor.ActiveNode = Node;
                return new ScriptStepResult(ScriptStepStatus.Ok, $"Active element: {Node.Path} ({Node.Kind}/{Node.Format})");
            }
        }
    }
}
