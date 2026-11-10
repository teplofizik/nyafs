using System;
using System.Collections.Generic;
using System.Linq;

namespace NyaFs.Processor.Scripting.Commands
{
    public class Help : ScriptStepGenerator
    {
        public Help() : base("help")
        {
            AddConfig(new ScriptArgsConfig(0, Array.Empty<ScriptArgsParam>()));
            AddConfig(new ScriptArgsConfig(1, new[] { new Params.StringScriptArgsParam("command-or-format") }));
        }

        public override ScriptStep Get(ScriptArgs Args) =>
            new HelpScriptStep(Args.ArgConfig == 1 ? Args.RawArgs[0] : null);

        public class HelpScriptStep : ScriptStep
        {
            private readonly string Topic;

            public HelpScriptStep(string Topic) : base("help")
            {
                this.Topic = Topic;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                if (!string.IsNullOrEmpty(Topic))
                {
                    var Generator = Processor.Parser.GetGenerator(Topic);
                    if (Generator != null)
                    {
                        Log.Write(0, $"{Generator.Name}:");
                        foreach (var Config in Generator.GetConfigs())
                        {
                            var Args = Config.Params == null ? "" : String.Join(" ", Config.Params.Select(P => "<" + P.Name + ">"));
                            Log.Write(0, $"  {Generator.Name} {Args}".TrimEnd());
                        }
                        return new ScriptStepResult(ScriptStepStatus.Ok, null);
                    }

                    var FsPlugin = Processor.Plugins.ResolveFilesystem(Topic);
                    if (FsPlugin != null)
                    {
                        Log.Write(0, $"{FsPlugin.Format} ({FsPlugin.Name}):");
                        if (FsPlugin.Options.Length == 0)
                            Log.Write(0, "  no options");
                        foreach (var Option in FsPlugin.Options)
                        {
                            string Allowed = (Option.Type == NyaFs.ImageFormat.Plugins.Base.FilesystemOptionType.Enum)
                                ? " [" + String.Join("|", Option.AllowedValues) + "]"
                                : "";
                            string Default = (Option.DefaultValue != null) ? $" default={Option.DefaultValue}" : "";
                            string Description = (Option.Description != null) ? " - " + Option.Description : "";
                            Log.Write(0, $"  {Option.Name} <{Option.Type}>{Allowed}{Default}{Description}");
                        }
                        return new ScriptStepResult(ScriptStepStatus.Ok, null);
                    }

                    var KernelPlugin = Processor.Plugins.ResolveKernel(Topic);
                    if (KernelPlugin != null)
                    {
                        Log.Write(0, $"{KernelPlugin.Format} ({KernelPlugin.Name}) [kernel]:");
                        PrintOptions(KernelPlugin.Options);
                        return new ScriptStepResult(ScriptStepStatus.Ok, null);
                    }

                    var DtbPlugin = Processor.Plugins.ResolveDtb(Topic);
                    if (DtbPlugin != null)
                    {
                        Log.Write(0, $"{DtbPlugin.Format} ({DtbPlugin.Name}) [devtree]:");
                        PrintOptions(DtbPlugin.Options);
                        return new ScriptStepResult(ScriptStepStatus.Ok, null);
                    }

                    var ImagePlugin = Processor.Plugins.ResolveImage(Topic);
                    if (ImagePlugin != null)
                    {
                        Log.Write(0, $"{ImagePlugin.Format} ({ImagePlugin.Name}) [container]:");
                        PrintOptions(ImagePlugin.Options);
                        return new ScriptStepResult(ScriptStepStatus.Ok, null);
                    }

                    return new ScriptStepResult(ScriptStepStatus.Error, $"Unknown command or image plugin: {Topic}");
                }

                Log.Write(0, "Commands:");
                foreach (var Generator in Processor.Parser.GetGenerators().OrderBy(G => G.Name))
                    Log.Write(0, "  " + Generator.Name);

                Log.Write(0, "");
                Log.Write(0, "Filesystem formats and options:");
                foreach (var Plugin in Processor.Plugins.GetFilesystems().OrderBy(P => P.Format))
                {
                    var Meta = new List<string>();
                    if (Plugin.Aliases.Length > 0) Meta.Add("aliases: " + String.Join(", ", Plugin.Aliases));
                    if (Plugin.Extensions.Length > 0) Meta.Add("ext: " + String.Join(", ", Plugin.Extensions));
                    var Suffix = (Meta.Count > 0) ? " [" + String.Join("; ", Meta) + "]" : "";
                    Log.Write(0, $"  {Plugin.Format} ({Plugin.Name}){Suffix}");
                    foreach (var Option in Plugin.Options)
                        Log.Write(0, $"    {Option.Name} <{Option.Type}>");
                }

                Log.Write(0, "");
                Log.Write(0, "Kernel formats and options:");
                foreach (var Plugin in Processor.Plugins.GetKernels().OrderBy(P => P.Format))
                {
                    Log.Write(0, $"  {Plugin.Format} ({Plugin.Name})");
                    foreach (var Option in Plugin.Options)
                        Log.Write(0, $"    {Option.Name} <{Option.Type}>");
                }

                Log.Write(0, "");
                Log.Write(0, "Device-tree formats and options:");
                foreach (var Plugin in Processor.Plugins.GetDtbs().OrderBy(P => P.Format))
                {
                    Log.Write(0, $"  {Plugin.Format} ({Plugin.Name})");
                    foreach (var Option in Plugin.Options)
                        Log.Write(0, $"    {Option.Name} <{Option.Type}>");
                }

                Log.Write(0, "");
                Log.Write(0, "Use: help <command>, help <filesystem-format>, help <kernel-format> or help <devtree-format>");
                return new ScriptStepResult(ScriptStepStatus.Ok, null);
            }

            private static void PrintOptions(NyaFs.ImageFormat.Plugins.Base.FilesystemOption[] Options)
            {
                if (Options.Length == 0)
                {
                    Log.Write(0, "  no options");
                    return;
                }

                foreach (var Option in Options)
                {
                    string Allowed = (Option.Type == NyaFs.ImageFormat.Plugins.Base.FilesystemOptionType.Enum)
                        ? " [" + String.Join("|", Option.AllowedValues) + "]"
                        : "";
                    string Default = (Option.DefaultValue != null) ? $" default={Option.DefaultValue}" : "";
                    string Description = (Option.Description != null) ? " - " + Option.Description : "";
                    Log.Write(0, $"  {Option.Name} <{Option.Type}>{Allowed}{Default}{Description}");
                }
            }
        }
    }
}
