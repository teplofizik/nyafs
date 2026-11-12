using NyaFs.Keys;
using NyaFs.Processor;
using NyaFs.Processor.Scripting.Helper;
using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>Key profile management (list/use/import/remove).</summary>
    public class Keys : ScriptStepGenerator
    {
        public Keys() : base("keys")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "list" })
            }));
            AddConfig(new ScriptArgsConfig(1, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "use", "remove" }),
                new Params.StringScriptArgsParam("profile")
            }));
            AddConfig(new ScriptArgsConfig(2, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "import" }),
                new Params.StringScriptArgsParam("profile"),
                new Params.LocalPathScriptArgsParam(),
                new Params.LocalPathScriptArgsParam()
            }));
            AddConfig(new ScriptArgsConfig(3, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "import-symmetric" }),
                new Params.StringScriptArgsParam("profile"),
                new Params.LocalPathScriptArgsParam()
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            var A = Args.RawArgs;
            switch (Args.ArgConfig)
            {
                case 0: return new KeysStep("list", null, null, null);
                case 1: return new KeysStep(A[0], A[1], null, null);
                case 2: return new KeysStep(A[0], A[1], A[2], A[3]);
                default: return new KeysStep(A[0], A[1], A[2], null);
            }
        }

        public class KeysStep : ScriptStep
        {
            private readonly string Action;
            private readonly string Profile;
            private readonly string PrivateFile;
            private readonly string CertificateFile;

            public KeysStep(string Action, string Profile, string PrivateFile, string CertificateFile) : base("keys")
            {
                this.Action = Action.ToLowerInvariant();
                this.Profile = Profile;
                this.PrivateFile = PrivateFile;
                this.CertificateFile = CertificateFile;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                try
                {
                    switch (Action)
                    {
                        case "list":
                            foreach (var Key in Processor.KeyStore.List())
                                Log.Write(0, Key.ToString());
                            return new ScriptStepResult(ScriptStepStatus.Ok, null);

                        case "use":
                            if (Processor.KeyStore.Find(Profile) == null)
                                return new ScriptStepResult(ScriptStepStatus.Error, "Key profile not found: " + Profile);
                            Processor.KeyStore.ActiveProfile = Profile;
                            return new ScriptStepResult(ScriptStepStatus.Ok, "Active key: " + Profile);

                        case "remove":
                            string RemoveError;
                            if (!Processor.KeyStore.Remove(Profile, out RemoveError))
                                return new ScriptStepResult(ScriptStepStatus.Error, RemoveError);
                            return new ScriptStepResult(ScriptStepStatus.Ok, "Removed key profile: " + Profile);

                        case "import":
                            {
                                var PrivatePath = this.DetectFilePath(PrivateFile);
                                var CertificatePath = this.DetectFilePath(CertificateFile);
                                if ((PrivatePath == null) || (CertificatePath == null))
                                    return new ScriptStepResult(ScriptStepStatus.Error, "Private key or certificate not found");

                                var Private = File.ReadAllBytes(PrivatePath);
                                var Certificate = new X509Certificate2(File.ReadAllBytes(CertificatePath));
                                var Record = new KeyRecord
                                {
                                    Id = Profile,
                                    Name = Profile,
                                    Kind = KeyKind.Asymmetric,
                                    Usage = KeyUsage.SignVerify,
                                    Algorithm = Certificate.PublicKey.Oid.FriendlyName ?? "",
                                    Fingerprint = FileKeyStore.Fingerprint(Certificate.RawData),
                                    Certificate = Certificate.RawData,
                                    PublicKey = Certificate.RawData,
                                    ProtectedPrivate = FileKeyStore.Protect(Private)
                                };

                                string Error;
                                if (!Processor.KeyStore.Save(Record, out Error))
                                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                                return new ScriptStepResult(ScriptStepStatus.Ok, "Imported key profile: " + Profile);
                            }

                        case "import-symmetric":
                            {
                                var SecretPath = this.DetectFilePath(PrivateFile);
                                if (SecretPath == null)
                                    return new ScriptStepResult(ScriptStepStatus.Error, "Secret file not found");

                                var Secret = File.ReadAllBytes(SecretPath);
                                var Record = new KeyRecord
                                {
                                    Id = Profile,
                                    Name = Profile,
                                    Kind = KeyKind.Symmetric,
                                    Usage = KeyUsage.EncryptDecrypt,
                                    Algorithm = "AES",
                                    Fingerprint = FileKeyStore.Fingerprint(Secret),
                                    ProtectedSecret = FileKeyStore.Protect(Secret)
                                };

                                string Error;
                                if (!Processor.KeyStore.Save(Record, out Error))
                                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                                return new ScriptStepResult(ScriptStepStatus.Ok, "Imported symmetric key profile: " + Profile);
                            }

                        default:
                            return new ScriptStepResult(ScriptStepStatus.Error, "Unknown keys action: " + Action);
                    }
                }
                catch (Exception E)
                {
                    return new ScriptStepResult(ScriptStepStatus.Error, "keys failed: " + E.Message);
                }
            }
        }
    }
}
