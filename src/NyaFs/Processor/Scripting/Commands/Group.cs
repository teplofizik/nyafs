using NyaFs.Filesystem.Universal.Identity;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// List and edit /etc/group entries.
    /// </summary>
    public class Group : ScriptStepGenerator
    {
        public Group() : base("group")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "list" })
            }));

            AddConfig(new ScriptArgsConfig(1, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "add" }),
                new Params.StringScriptArgsParam("name"),
                new Params.NumberScriptArgsParam("gid")
            }));

            AddConfig(new ScriptArgsConfig(2, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "del" }),
                new Params.StringScriptArgsParam("name")
            }));

            AddConfig(new ScriptArgsConfig(3, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "adduser" }),
                new Params.StringScriptArgsParam("user"),
                new Params.StringScriptArgsParam("group")
            }));

            AddConfig(new ScriptArgsConfig(4, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("command", new string[] { "deluser" }),
                new Params.StringScriptArgsParam("user"),
                new Params.StringScriptArgsParam("group")
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
                    return new AddScriptStep(A[1], UInt32.Parse(A[2], NumberStyles.None, CultureInfo.InvariantCulture));
                case 2:
                    return new RemoveScriptStep(A[1]);
                case 3:
                    return new MembershipScriptStep(A[1], A[2], true);
                default:
                    return new MembershipScriptStep(A[1], A[2], false);
            }
        }

        private class ListScriptStep : ScriptStep
        {
            public ListScriptStep() : base("group") { }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                var Db = Fs.Identities;
                Db.Load();
                if (!Db.GroupAvailable)
                    return new ScriptStepResult(ScriptStepStatus.Warning, FsIdentityDatabase.GroupPath + " is not present");

                foreach (var G in Db.Groups)
                    Log.Write(0, $"{G.Name,-20} {G.Gid,6} {String.Join(",", G.Members)}");

                return new ScriptStepResult(ScriptStepStatus.Ok, null);
            }
        }

        private class AddScriptStep : ScriptStep
        {
            private readonly string Name;
            private readonly uint Gid;

            public AddScriptStep(string Name, uint Gid) : base("group")
            {
                this.Name = Name;
                this.Gid = Gid;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                var Db = Fs.Identities;
                string Error;
                if (!Db.AddGroup(Name, Gid, out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                if (!Db.Save(out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);

                return new ScriptStepResult(ScriptStepStatus.Ok, $"Group {Name} ({Gid}) added!");
            }
        }

        private class RemoveScriptStep : ScriptStep
        {
            private readonly string Name;

            public RemoveScriptStep(string Name) : base("group")
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
                if (!Db.RemoveGroup(Name, out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                if (!Db.Save(out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);

                return new ScriptStepResult(ScriptStepStatus.Ok, $"Group {Name} removed!");
            }
        }

        private class MembershipScriptStep : ScriptStep
        {
            private readonly string UserName;
            private readonly string GroupName;
            private readonly bool Add;

            public MembershipScriptStep(string UserName, string GroupName, bool Add) : base("group")
            {
                this.UserName = UserName;
                this.GroupName = GroupName;
                this.Add = Add;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                var Db = Fs.Identities;
                string Error;
                var Ok = Add ? Db.AddUserToGroup(UserName, GroupName, out Error) :
                    Db.RemoveUserFromGroup(UserName, GroupName, out Error);
                if (!Ok)
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                if (!Db.Save(out Error))
                    return new ScriptStepResult(ScriptStepStatus.Error, Error);

                return new ScriptStepResult(ScriptStepStatus.Ok,
                    $"User {UserName} {(Add ? "added to" : "removed from")} group {GroupName}!");
            }
        }
    }
}
