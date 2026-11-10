using NyaFs.Filesystem.Universal.Identity;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// List and edit /etc/passwd entries. UID/GID remain the canonical identity;
    /// the name is resolved through the loaded filesystem identity database.
    /// </summary>
    public class User : ScriptStepGenerator
    {
        public User() : base("user")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "list" })
            }));

            AddConfig(new ScriptArgsConfig(1, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "add" }),
                new Params.StringScriptArgsParam("name"),
                new Params.NumberScriptArgsParam("uid"),
                new Params.NumberScriptArgsParam("gid")
            }));

            AddConfig(new ScriptArgsConfig(2, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "add" }),
                new Params.StringScriptArgsParam("name"),
                new Params.NumberScriptArgsParam("uid"),
                new Params.NumberScriptArgsParam("gid"),
                new Params.StringScriptArgsParam("home")
            }));

            AddConfig(new ScriptArgsConfig(3, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "add" }),
                new Params.StringScriptArgsParam("name"),
                new Params.NumberScriptArgsParam("uid"),
                new Params.NumberScriptArgsParam("gid"),
                new Params.StringScriptArgsParam("home"),
                new Params.StringScriptArgsParam("shell")
            }));

            AddConfig(new ScriptArgsConfig(4, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "del" }),
                new Params.StringScriptArgsParam("name")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            var A = Args.RawArgs;

            switch (Args.ArgConfig)
            {
                case 0:
                    return new ListScriptStep();
                case 1:
                    return new AddScriptStep(A[1], ParseId(A[2]), ParseId(A[3]), "/", "/bin/sh");
                case 2:
                    return new AddScriptStep(A[1], ParseId(A[2]), ParseId(A[3]), A[4], "/bin/sh");
                case 3:
                    return new AddScriptStep(A[1], ParseId(A[2]), ParseId(A[3]), A[4], A[5]);
                default:
                    return new RemoveScriptStep(A[1]);
            }
        }

        private static uint ParseId(string Value) =>
            UInt32.Parse(Value, NumberStyles.None, CultureInfo.InvariantCulture);

        private class ListScriptStep : ScriptStep
        {
            public ListScriptStep() : base("user") { }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                var Db = Fs.Identities;
                Db.Load();
                if (!Db.PasswdAvailable)
                    return new ScriptStepResult(ScriptStepStatus.Warning, FsIdentityDatabase.PasswdPath + " is not present");

                foreach (var U in Db.Users)
                    Log.Write(0, $"{U.Name,-20} {U.Uid,6} {U.Gid,6} {U.Home,-24} {U.Shell}");

                return new ScriptStepResult(ScriptStepStatus.Ok, null);
            }
        }

        private class AddScriptStep : ScriptStep
        {
            private readonly string Name;
            private readonly uint Uid;
            private readonly uint Gid;
            private readonly string Home;
            private readonly string Shell;

            public AddScriptStep(string Name, uint Uid, uint Gid, string Home, string Shell) : base("user")
            {
                this.Name = Name;
                this.Uid = Uid;
                this.Gid = Gid;
                this.Home = Home;
                this.Shell = Shell;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                var Db = Fs.Identities;
                string Error;
                if (!Db.AddUser(Name, Uid, Gid, "", Home, Shell, out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                if (!Db.Save(out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);

                return new ScriptStepResult(ScriptStepStatus.Ok, $"User {Name} ({Uid}:{Gid}) added!");
            }
        }

        private class RemoveScriptStep : ScriptStep
        {
            private readonly string Name;

            public RemoveScriptStep(string Name) : base("user")
            {
                this.Name = Name;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                var Db = Fs.Identities;
                string Error;
                if (!Db.RemoveUser(Name, out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                if (!Db.Save(out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);

                return new ScriptStepResult(ScriptStepStatus.Ok, $"User {Name} removed!");
            }
        }
    }
}
