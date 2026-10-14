using NyaFs.ImageFormat.Elements.Dtb;
using NyaFs.ImageFormat.Elements.Fs;
using NyaFs.ImageFormat.Elements.Kernel;
using NyaFs.ImageFormat.Plugins.Base;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor
{
    public class ImageProcessor
    {
        public string ActivePath = "/";

        /// <summary>
        /// Session images blob
        /// </summary>
        ImageFormat.BaseImageBlob Blob = new ImageFormat.BaseImageBlob();

        /// <summary>
        /// Session variable scope
        /// </summary>
        public Scripting.Variables.VariableScope Scope = new Scripting.Variables.VariableScope();

        /// <summary>
        /// Session plugins
        /// </summary>
        public Scripting.ScriptPlugins Plugins = new Scripting.ScriptPlugins();

        /// <summary>
        /// Session parser
        /// </summary>
        public Scripting.ScriptParser Parser;

        public ImageProcessor(Scripting.ScriptParser Parser, string[] PluginsDirs, bool LoadAll = false)
        {
            this.Parser = Parser;
            LoadInternalPlugins();

            var Loaded = new HashSet<string>();
            foreach (string Dir in PluginsDirs)
            {
                if (string.IsNullOrEmpty(Dir))
                    continue;

                string Full;
                try { Full = System.IO.Path.GetFullPath(Dir); } catch { continue; }

                if (!Loaded.Add(Full))
                    continue;

                if (System.IO.Directory.Exists(Full))
                {
                    LoadPlugins(Full);

                    if (!LoadAll)
                        break;
                }
            }
        }

        public ImageProcessor(Scripting.ScriptParser Parser) : this(Parser, new string[] { "plugins" })
        {

        }

        public void SetFs(LinuxFilesystem Fs) => Blob.SetFilesystem(0, Fs);

        public void SetKernel(LinuxKernel Kernel) => Blob.SetKernel(0, Kernel);

        public void SetDeviceTree(DeviceTree Dtb) => Blob.SetDevTree(0, Dtb);

        public void Reset()
        {
            Blob = new ImageFormat.BaseImageBlob();
        }

        public LinuxKernel GetKernel(bool CreateIfNotExists = false)
        {
            var Res = Blob.GetKernel(0);

            if ((Res == null) && CreateIfNotExists)
            {
                Res = new LinuxKernel();
                Blob.SetKernel(0, Res);
            }

            return Res;
        }

        public LinuxFilesystem GetFs(bool CreateIfNotExists = false)
        {
            var Res = Blob.GetFilesystem(0);

            if ((Res == null) && CreateIfNotExists)
            {
                Res = new LinuxFilesystem();
                Blob.SetFilesystem(0, Res);
            }

            return Res;
        }

        public DeviceTree GetDevTree(bool CreateIfNotExists = false)
        {
            var Res = Blob.GetDevTree(0);

            if((Res == null) && CreateIfNotExists)
            {
                Res = new DeviceTree();
                Blob.SetDevTree(0, Res);
            }

            return Res;
        }

        public ImageFormat.BaseImageBlob GetBlob() => Blob;

        public bool IsFsLoaded => Blob.IsProvidedFs;

        public void Process(Scripting.Script Script)
        {
            foreach(var S in Script.Steps)
            {
                if (S.CheckCondition(this))
                {
                    var Step = S.GetPreprocessed(Scope);
                    if (Step == null)
                        continue;

                    var Res = Step.Exec(this);

                    WriteLogLine(S, Res);
                }
            }
        }

        private void WriteLogLine(Scripting.ScriptStep Step, Scripting.ScriptStepResult Res)
        {
            if (Res.Text != null)
            {
                switch (Res.Status)
                {
                    case ScriptStepStatus.Error: Log.Error(0, $"{Step.Name} [{Step.ScriptName}:{Step.ScriptLine}]: {Res.Text}"); break;
                    case ScriptStepStatus.Ok: Log.Ok(0, $"{Step.Name}: {Res.Text}"); break;
                    case ScriptStepStatus.Warning: Log.Warning(0, $"{Step.Name} [{Step.ScriptName}:{Step.ScriptLine}]: {Res.Text}"); break;
                }
            }
        }

        private void ProcessPluginList(NyaPlugin[] PluginList)
        {
            foreach (var Plugin in PluginList)
            {
                if (Plugins.Load(Plugin))
                {
                    var CP = Plugin as Scripting.Plugins.CommandPlugin;
                    if (CP != null)
                    {
                        var Generators = CP.GetGenerators();

                        Parser.AddGenerators(Generators);
                    }
                }
            }
        }

        public void LoadInternalPlugins()
        {
            var PluginList = new List<NyaPlugin>();

            PluginList.AddRange(ImageFormat.Plugins.BuiltinPlugins.Images);
            PluginList.AddRange(ImageFormat.Plugins.BuiltinPlugins.Filesystems);
            PluginList.AddRange(ImageFormat.Plugins.BuiltinPlugins.Kernels);
            PluginList.AddRange(ImageFormat.Plugins.BuiltinPlugins.Compressors);

            ProcessPluginList(PluginList.ToArray());
        }

        public void LoadPlugins(string Path)
        {
            var Filenames = System.IO.Directory.GetFiles($"{Path}/", "*.dll");

            foreach (var F in Filenames)
            {
                try
                {
                    var PluginList = Plugins.LoadFromFile(F);
                    ProcessPluginList(PluginList);
                }
                catch (Exception E)
                {
                    Log.Error(0, $"Error on loading {F}: {E.Message}");
                }
            }
        }
    }
}
