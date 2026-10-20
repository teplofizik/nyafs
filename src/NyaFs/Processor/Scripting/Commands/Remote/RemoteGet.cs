using NyaFs.Processor;
using NyaFs.Remote;
using System;

namespace NyaFs.Processor.Scripting.Commands.Remote
{
    public sealed class RemoteGet : ScriptStepGenerator
    {
        public RemoteGet() : base("remoteget")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.StringScriptArgsParam("protocol"),
                new Params.StringScriptArgsParam("host"),
                new Params.StringScriptArgsParam("login"),
                new Params.StringScriptArgsParam("pass"),
                new Params.FsPathScriptArgsParam(),
                new Params.StringScriptArgsParam("remotepath")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args) =>
            new Step(Args.RawArgs[0], Args.RawArgs[1], Args.RawArgs[2], Args.RawArgs[3], Args.RawArgs[4], Args.RawArgs[5]);

        private sealed class Step : ScriptStep
        {
            private readonly string Protocol;
            private readonly string Host;
            private readonly string Login;
            private readonly string Password;
            private readonly string FsPath;
            private readonly string RemotePath;

            public Step(string Protocol, string Host, string Login, string Password, string FsPath, string RemotePath)
                : base("remoteget")
            {
                this.Protocol = Protocol;
                this.Host = Host;
                this.Login = Login;
                this.Password = Password;
                this.FsPath = FsPath;
                this.RemotePath = RemotePath;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                var Fs = Processor.GetFs();
                if (Fs == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                var Plugin = Processor.Plugins.GetRemote(Protocol);
                if (Plugin == null)
                    return new ScriptStepResult(ScriptStepStatus.Error,
                        $"Protocol '{Protocol}' is not available: no remote plugin is loaded");

                try
                {
                    var Auth = RemoteAuthResolver.Resolve(Login, Password);
                    using (var Remote = Plugin.Create(Protocol, Host, Auth))
                    {
                        Remote.Connect();
                        RemoteTransfer.Download(Remote, RemotePath, Fs, FsPath);
                    }

                    return new ScriptStepResult(ScriptStepStatus.Ok, null);
                }
                catch (Exception E)
                {
                    return new ScriptStepResult(ScriptStepStatus.Error,
                        $"remoteget {Protocol} {Host} {RemotePath} -> {FsPath}: {E.Message}");
                }
            }
        }
    }
}
