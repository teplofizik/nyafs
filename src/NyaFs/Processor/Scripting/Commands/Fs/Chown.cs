using NyaFs.Processor.Scripting;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Commands.Fs
{
    public class Chown : ScriptStepGenerator
    {
        public Chown() : base("chown")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.FsPathScriptArgsParam(),
                new Params.StringScriptArgsParam("user")
            }));
            AddConfig(new ScriptArgsConfig(1, new ScriptArgsParam[]
            {
                new Params.FsPathScriptArgsParam(),
                new Params.StringScriptArgsParam("user"),
                new Params.StringScriptArgsParam("group")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            if(Args.ArgConfig == 0)
                return new ChownScriptStepUser(Args.RawArgs[0], Args.RawArgs[1]);
            else
                return new ChownScriptStepUserGroup(Args.RawArgs[0], Args.RawArgs[1], Args.RawArgs[2]);
        }

        internal static bool TryResolveUser(NyaFs.ImageFormat.Elements.Fs.LinuxFilesystem Fs, string Value, out uint Id, out string Error)
        {
            if (Fs.Identities.TryGetUserId(Value, out Id))
            {
                Error = null;
                return true;
            }

            Error = "User '" + Value + "' not found in /etc/passwd";
            return false;
        }

        internal static bool TryResolveGroup(NyaFs.ImageFormat.Elements.Fs.LinuxFilesystem Fs, string Value, out uint Id, out string Error)
        {
            if (Fs.Identities.TryGetGroupId(Value, out Id))
            {
                Error = null;
                return true;
            }

            Error = "Group '" + Value + "' not found in /etc/group";
            return false;
        }

        public class ChownScriptStepUser : ScriptStep
        {
            string Path;
            string User;

            public ChownScriptStepUser(string Path, string User) : base("chown")
            {
                this.Path = Path;
                this.User = User;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                if (Fs.Exists(Path))
                {
                    var UserName = User;
                    var GroupName = (string)null;
                    var Separator = User.IndexOf(':'); 
                    if (Separator >= 0)
                    {
                        UserName = User.Substring(0, Separator);
                        GroupName = User.Substring(Separator + 1);
                    }

                    uint Uid;
                    string Error;
                    if (!TryResolveUser(Fs, UserName, out Uid, out Error))
                        return new ScriptStepResult(ScriptStepStatus.Error, Error);

                    uint Gid = 0;
                    var HasGroup = !String.IsNullOrEmpty(GroupName);
                    if (HasGroup && !TryResolveGroup(Fs, GroupName, out Gid, out Error))
                        return new ScriptStepResult(ScriptStepStatus.Error, Error);

                    var Item = Fs.GetElement(Path);
                    Item.User = Uid;
                    if (HasGroup)
                        Item.Group = Gid;

                    return new ScriptStepResult(ScriptStepStatus.Ok,
                        HasGroup ? $"Owner of {Path} is updated to {UserName} (uid {Uid}), group is to {GroupName} (gid {Gid})!"
                                 : $"Owner of {Path} is updated to {UserName} (uid {Uid})!");
                }
                else
                    return new ScriptStepResult(ScriptStepStatus.Warning, $"{Path} not found!");
            }
        }

        public class ChownScriptStepUserGroup : ScriptStep
        {
            string Path;
            string User;
            string Group;

            public ChownScriptStepUserGroup(string Path, string User, string Group) : base("chown")
            {
                this.Path = Path;
                this.User = User;
                this.Group = Group;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                if (Fs.Exists(Path))
                {
                    uint Uid;
                    uint Gid;
                    string Error;
                    if (!TryResolveUser(Fs, User, out Uid, out Error))
                        return new ScriptStepResult(ScriptStepStatus.Error, Error);
                    if (!TryResolveGroup(Fs, Group, out Gid, out Error))
                        return new ScriptStepResult(ScriptStepStatus.Error, Error);

                    var Item = Fs.GetElement(Path);
                    Item.User = Uid;
                    Item.Group = Gid;

                    return new ScriptStepResult(ScriptStepStatus.Ok, $"Owner of {Path} is updated to {User} (uid {Uid}), group is to {Group} (gid {Gid})!");
                }
                else
                    return new ScriptStepResult(ScriptStepStatus.Warning, $"{Path} not found!");
            }
        }
    }
}
