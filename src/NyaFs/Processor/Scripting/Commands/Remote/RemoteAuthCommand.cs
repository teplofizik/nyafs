using NyaFs.Processor;
using NyaFs.Remote;
using System;

namespace NyaFs.Processor.Scripting.Commands.Remote
{
    public sealed class RemoteAuthCommand : ScriptStepGenerator
    {
        public RemoteAuthCommand() : base("remoteauth")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[] { }));

            AddConfig(new ScriptArgsConfig(1, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "list", "path" })
            }));

            AddConfig(new ScriptArgsConfig(2, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "show", "remove" }),
                new Params.StringScriptArgsParam("name")
            }));

            AddConfig(new ScriptArgsConfig(3, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "add" }),
                new Params.StringScriptArgsParam("name"),
                new Params.StringScriptArgsParam("login"),
                new Params.StringScriptArgsParam("password")
            }));

            AddConfig(new ScriptArgsConfig(4, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "addkey" }),
                new Params.StringScriptArgsParam("name"),
                new Params.StringScriptArgsParam("login"),
                new Params.StringScriptArgsParam("key")
            }));

            AddConfig(new ScriptArgsConfig(5, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "addkey" }),
                new Params.StringScriptArgsParam("name"),
                new Params.StringScriptArgsParam("login"),
                new Params.StringScriptArgsParam("key"),
                new Params.StringScriptArgsParam("passphrase")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            switch (Args.ArgConfig)
            {
                case 0:
                    return new Step("list", null, null, null, null);
                case 1:
                    return new Step(Args.RawArgs[0], null, null, null, null);
                case 2:
                    return new Step(Args.RawArgs[0], Args.RawArgs[1], null, null, null);
                case 3:
                    return new Step("add", Args.RawArgs[1], Args.RawArgs[2], Args.RawArgs[3], null);
                case 4:
                    return new Step("addkey", Args.RawArgs[1], Args.RawArgs[2], Args.RawArgs[3], null);
                default:
                    return new Step("addkey", Args.RawArgs[1], Args.RawArgs[2], Args.RawArgs[3], Args.RawArgs[4]);
            }
        }

        private sealed class Step : ScriptStep
        {
            private readonly string Action;
            private readonly string Name;
            private readonly string Login;
            private readonly string Value;
            private readonly string Passphrase;

            public Step(string Action, string Name, string Login, string Value, string Passphrase)
                : base("remoteauth")
            {
                this.Action = Action;
                this.Name = Name;
                this.Login = Login;
                this.Value = Value;
                this.Passphrase = Passphrase;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                try
                {
                    var Store = RemoteAuthStore.Default;

                    switch (Action)
                    {
                        case "list":
                            PrintList(Store);
                            return new ScriptStepResult(ScriptStepStatus.Ok, null);

                        case "path":
                            Log.Write(0, Store.Location);
                            return new ScriptStepResult(ScriptStepStatus.Ok, null);

                        case "show":
                            {
                                var Auth = Store.Get(Name);
                                Log.Write(0, $"{Name}: login={Auth.Login}, auth={Auth.Kind}, key={Auth.PrivateKeyPath ?? "-"}");
                                return new ScriptStepResult(ScriptStepStatus.Ok, null);
                            }

                        case "remove":
                            if (!Store.Remove(Name))
                                return new ScriptStepResult(ScriptStepStatus.Error, $"Auth profile '{Name}' was not found");

                            Log.Write(0, $"Auth profile '{Name}' removed");
                            return new ScriptStepResult(ScriptStepStatus.Ok, null);

                        case "add":
                            {
                                var Auth = RemoteAuth.FromPassword(Login, Value == "-" ? "" : Value);
                                Store.Set(Name, Auth);
                                Log.Write(0, $"Auth profile '{Name}' saved ({Auth.Kind})");
                                return new ScriptStepResult(ScriptStepStatus.Ok, null);
                            }

                        case "addkey":
                            {
                                var Pass = Passphrase == "-" ? "" : Passphrase;
                                var Auth = RemoteAuth.FromPrivateKey(Login, Value, Pass);
                                Store.Set(Name, Auth);
                                Log.Write(0, $"Auth profile '{Name}' saved ({Auth.Kind})");
                                return new ScriptStepResult(ScriptStepStatus.Ok, null);
                            }

                        default:
                            return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown remoteauth action '{Action}'");
                    }
                }
                catch (Exception E)
                {
                    return new ScriptStepResult(ScriptStepStatus.Error, $"remoteauth {Action}: {E.Message}");
                }
            }

            private static void PrintList(ICredentialStore Store)
            {
                var Profiles = Store.List();
                Log.Write(0, "NAME                 LOGIN                AUTH          KEY");

                foreach (var Profile in Profiles)
                {
                    var Auth = Profile.Auth;
                    Log.Write(0,
                        $"{Profile.Name.PadRight(20)} {(Auth.Login ?? "").PadRight(20)} {Auth.Kind.PadRight(13)} {Auth.PrivateKeyPath ?? "-"}");
                }
            }
        }
    }
}
