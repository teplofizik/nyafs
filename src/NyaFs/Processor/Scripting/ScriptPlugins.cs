using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Plugins;
using NyaFs.ImageFormat.Plugins.Base;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NyaFs.Processor.Scripting
{
    /// <summary>
    /// Session-facing plugin facade. The storage and detection logic lives in
    /// <see cref="PluginRegistry"/>; this class adds dynamic DLL loading.
    /// </summary>
    public class ScriptPlugins : PluginRegistry
    {
        public ScriptPlugins()
        {
            PluginHost.SetCurrent(this);
        }

        public NyaPlugin[] LoadFromFile(string Filename)
        {
#if NETSTANDARD2_0
            // Dynamic assembly loading is not available on netstandard2.0.
            Log.Warning(0, "Dynamic plugin loading is not supported on netstandard2.0.");
            return new NyaPlugin[0];
#else
            var Res = new List<NyaPlugin>();
            var PluginAssembly = Plugins.PluginLoader.LoadPlugin(Filename);

            if (PluginAssembly != null)
            {
                var Types = PluginAssembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface && CheckPluginType(t)).ToArray();
                foreach (var t in Types)
                {
                    var obj = AppDomain.CurrentDomain.CreateInstanceFrom(Filename, t.FullName).Unwrap();
                    var Plugin = obj as NyaPlugin;
                    if (Plugin != null)
                        Res.Add(Plugin);
                }
            }
            return Res.ToArray();
#endif
        }

        private bool CheckPluginType(Type type)
        {
            if (type == null) return false;
            if (type.FullName == "NyaFs.ImageFormat.Plugins.Base.NyaPlugin") return true;
            if (type.IsInterface) return false;

            return CheckPluginType(type.BaseType);
        }
    }
}
