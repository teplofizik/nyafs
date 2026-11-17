using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Kernel
{
    public class LinuxKernel
    {
        /// <summary>
        /// Image information, arch or supported os
        /// </summary>
        public Types.ImageInfo Info = new Types.ImageInfo();

        /// <summary>
        /// Несжатый образ ядра
        /// </summary>
        public byte[] Image = null;

        /// <summary>Canonical format reported by the loaded kernel plugin.</summary>
        public string KernelFormat;

        private readonly Dictionary<string, string> PluginOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void SetKernelOption(string Format, string Name, string Value) =>
            PluginOptions[Format + "." + Name] = Value;

        public string GetKernelOption(string Format, string Name, string DefaultValue = null) =>
            PluginOptions.TryGetValue(Format + "." + Name, out string Value) ? Value : DefaultValue;

        /// <summary>
        /// ����㦥�� �� ��
        /// </summary>
        public bool Loaded => (Image != null);
    }
}
