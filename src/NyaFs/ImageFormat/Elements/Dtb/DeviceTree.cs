using NyaFs.FlattenedDeviceTree;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Dtb
{
    public class DeviceTree
    {
        /// <summary>
        /// Image information, arch or supported os
        /// </summary>
        public Types.ImageInfo Info = new Types.ImageInfo();

        /// <summary>
        /// Loaded device tree
        /// </summary>
        public FlattenedDeviceTree.FlattenedDeviceTree DevTree = new FlattenedDeviceTree.FlattenedDeviceTree();

        /// <summary>Canonical format reported by the loaded device-tree plugin.</summary>
        public string DevtreeFormat;

        private readonly Dictionary<string, string> PluginOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void SetDevtreeOption(string Format, string Name, string Value) =>
            PluginOptions[Format + "." + Name] = Value;

        public string GetDevtreeOption(string Format, string Name, string DefaultValue = null) =>
            PluginOptions.TryGetValue(Format + "." + Name, out string Value) ? Value : DefaultValue;

        /// <summary>
        /// Is device tree loaded
        /// </summary>
        public bool Loaded => DevTree.Root.Nodes.Count > 0;

        public DeviceTree()
        {

        }
    }
}
