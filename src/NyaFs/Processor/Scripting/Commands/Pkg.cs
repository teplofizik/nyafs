using System;
using System.IO;
using System.Text;
using NyaFs.Packages;
using NyaFs.Processor.Scripting.Helper;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// Package commands: pkg info/add &lt;file&gt;, pkg create &lt;file&gt; &lt;fs-path&gt;
    /// &lt;name&gt; &lt;version&gt; [compression], pkg createdir &lt;file&gt; &lt;local-dir&gt;
    /// &lt;name&gt; &lt;version&gt; [compression].
    /// </summary>
    public class Pkg : ScriptStepGenerator
    {
        public Pkg() : base("pkg")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "info", "add" }),
                new Params.StringScriptArgsParam("file")
            }));

            AddConfig(new ScriptArgsConfig(1, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "create" }),
                new Params.StringScriptArgsParam("file"),
                new Params.FsPathScriptArgsParam(),
                new Params.StringScriptArgsParam("name"),
                new Params.StringScriptArgsParam("version")
            }));

            AddConfig(new ScriptArgsConfig(2, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "create" }),
                new Params.StringScriptArgsParam("file"),
                new Params.FsPathScriptArgsParam(),
                new Params.StringScriptArgsParam("name"),
                new Params.StringScriptArgsParam("version"),
                new Params.StringScriptArgsParam("compression")
            }));

            AddConfig(new ScriptArgsConfig(3, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "createdir" }),
                new Params.StringScriptArgsParam("file"),
                new Params.StringScriptArgsParam("directory"),
                new Params.StringScriptArgsParam("name"),
                new Params.StringScriptArgsParam("version")
            }));

            AddConfig(new ScriptArgsConfig(5, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "adddir" }),
                new Params.StringScriptArgsParam("directory"),
                new Params.StringScriptArgsParam("root-package")
            }));

            AddConfig(new ScriptArgsConfig(4, new ScriptArgsParam[]
            {
                new Params.EnumScriptArgsParam("action", new[] { "createdir" }),
                new Params.StringScriptArgsParam("file"),
                new Params.StringScriptArgsParam("directory"),
                new Params.StringScriptArgsParam("name"),
                new Params.StringScriptArgsParam("version"),
                new Params.StringScriptArgsParam("compression")
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            var A = Args.RawArgs;
            if (Args.ArgConfig == 5)
                return new PkgScriptStep(A[0], null, A[1], A[2], null, null);

            var Compression = (Args.ArgConfig == 2 || Args.ArgConfig == 4) ? A[5] : null;
            if (Args.ArgConfig == 0)
                return new PkgScriptStep(A[0], A[1], null, null, null, null);

            return new PkgScriptStep(A[0], A[1], A[2], A[3], A[4], Compression);
        }

        public class PkgScriptStep : ScriptStep
        {
            private readonly string Action;
            private readonly string File;
            private readonly string Path;
            private readonly string PkgName;
            private readonly string PkgVersion;
            private readonly string Compression;

            public PkgScriptStep(string Action, string File, string Path, string Name, string Version, string Compression) : base("pkg")
            {
                this.Action = Action.ToLowerInvariant();
                this.File = File;
                this.Path = Path;
                this.PkgName = Name;
                this.PkgVersion = Version;
                this.Compression = Compression;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                try
                {
                    var Resolved = Resolve(File);

                    switch (Action)
                    {
                        case "info":
                            {
                                var P = NyaPackage.Open(System.IO.File.ReadAllBytes(Resolved));
                                return new ScriptStepResult(ScriptStepStatus.Ok,
                                    $"Package: {P.Metadata} entries={P.Entries.Count}");
                            }
                        case "add":
                            {
                                var Fs = Processor.GetFs();
                                if (Fs == null)
                                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");

                                var P = NyaPackage.Open(System.IO.File.ReadAllBytes(Resolved));
                                if (!P.Matches(Fs))
                                    return new ScriptStepResult(ScriptStepStatus.Warning,
                                        $"Package {P.Metadata} does not match target ({Fs.Info.Architecture}/{Fs.Info.OperatingSystem}), skipped");

                                var Db = PackageDatabase.Load(Fs);
                                string Error;
                                if (!Db.CheckInstall(P.Metadata, out Error))
                                    return new ScriptStepResult(ScriptStepStatus.Error, Error);

                                int Count = P.AddTo(Fs, "/");
                                Db.Set(P.Metadata);
                                if (!Db.Save(Fs, out Error))
                                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                                return new ScriptStepResult(ScriptStepStatus.Ok,
                                    $"Installed {Count} items from package {P.Metadata}");
                            }
                        case "create":
                            {
                                var Fs = Processor.GetFs();
                                if (Fs == null)
                                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");
                                if (string.IsNullOrEmpty(Path) || string.IsNullOrEmpty(PkgName))
                                    return new ScriptStepResult(ScriptStepStatus.Error,
                                        "Usage: pkg create <file> <fs-path> <name> <version> [gzip|zstd]");

                                var Meta = new PackageMetadata { Name = PkgName, Version = PkgVersion ?? "" };
                                if (Fs.Info.Architecture != ImageFormat.Types.CPU.IH_ARCH_INVALID)
                                    Meta.Arch = Fs.Info.Architecture.ToString().Replace("IH_ARCH_", "").ToLowerInvariant();
                                if (Fs.Info.OperatingSystem != ImageFormat.Types.OS.IH_OS_INVALID)
                                    Meta.Os = Fs.Info.OperatingSystem.ToString().Replace("IH_OS_", "").ToLowerInvariant();

                                var P = NyaPackage.FromFilesystem(Fs, Path, Meta);
                                System.IO.File.WriteAllBytes(Resolved, P.ToFile(Compression));
                                return new ScriptStepResult(ScriptStepStatus.Ok,
                                    $"Created package {Resolved}: {P.Metadata}, entries={P.Entries.Count}");
                            }
                        case "adddir":
                            {
                                var Dir = this.DetectDirPath(Path);
                                if (Dir == null)
                                    return new ScriptStepResult(ScriptStepStatus.Error, "Directory not found: " + Path);

                                var Candidates = new System.Collections.Generic.List<NyaPackage>();
                                foreach (var PackageFile in System.IO.Directory.GetFiles(Dir, "*.nyapkg"))
                                    Candidates.Add(NyaPackage.Open(System.IO.File.ReadAllBytes(PackageFile)));

                                System.Collections.Generic.List<NyaPackage> Order;
                                string Error;
                                if (!PackageDatabase.Resolve(Candidates, PkgName, out Order, out Error))
                                    return new ScriptStepResult(ScriptStepStatus.Error, Error);

                                var Fs = Processor.GetFs();
                                if (Fs == null)
                                    return new ScriptStepResult(ScriptStepStatus.Error, "Filesystem is not loaded");
                                var Db = PackageDatabase.Load(Fs);
                                var Total = 0;
                                foreach (var Package in Order)
                                {
                                    if (!Db.CheckInstall(Package.Metadata, out Error))
                                        return new ScriptStepResult(ScriptStepStatus.Error, Error);
                                    Total += Package.AddTo(Fs, "/");
                                    Db.Set(Package.Metadata);
                                }
                                if (!Db.Save(Fs, out Error))
                                    return new ScriptStepResult(ScriptStepStatus.Error, Error);
                                return new ScriptStepResult(ScriptStepStatus.Ok,
                                    $"Installed {Order.Count} packages ({Total} items) from {Dir}");
                            }                        case "createdir":
                            {
                                if (string.IsNullOrEmpty(Path) || string.IsNullOrEmpty(PkgName))
                                    return new ScriptStepResult(ScriptStepStatus.Error,
                                        "Usage: pkg createdir <file> <local-dir> <name> <version> [gzip|zstd]");

                                var Dir = this.DetectDirPath(Path);
                                if (Dir == null)
                                    return new ScriptStepResult(ScriptStepStatus.Error, "Directory not found: " + Path);

                                var Meta = new PackageMetadata { Name = PkgName, Version = PkgVersion ?? "" };
                                var P = NyaPackage.FromDirectory(Dir, Meta);
                                System.IO.File.WriteAllBytes(Resolved, P.ToFile(Compression));
                                return new ScriptStepResult(ScriptStepStatus.Ok,
                                    $"Created package {Resolved}: {P.Metadata}, entries={P.Entries.Count}");
                            }
                        default:
                            return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown pkg action: {Action}");
                    }
                }
                catch (Exception E)
                {
                    return new ScriptStepResult(ScriptStepStatus.Error, $"pkg failed: {E.Message}");
                }
            }

            private string Resolve(string F)
            {
                if (string.IsNullOrEmpty(F) || System.IO.Path.IsPathRooted(F))
                    return F;

                var Dir = System.IO.Path.GetDirectoryName(ScriptFilename ?? "");
                return string.IsNullOrEmpty(Dir) ? F : System.IO.Path.Combine(Dir, F);
            }
        }
    }
}
