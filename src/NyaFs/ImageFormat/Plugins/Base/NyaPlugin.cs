using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.Base
{
    public class NyaPlugin
    {
        public readonly string Name;
        public readonly string PluginType;

        public NyaPlugin(string Name, string Type) 
        {
            this.Name = Name;
            this.PluginType = Type;
        }

        /// <summary>
        /// Detection priority. Plugins with a higher priority are probed before lower ones,
        /// so "override" plugins (encryption/signature wrappers) can intercept a load before
        /// the regular loader of the same container. Default 0.
        /// </summary>
        public virtual int Priority => 0;
    }
}
