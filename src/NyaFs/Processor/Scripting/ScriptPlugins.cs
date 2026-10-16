using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Plugins.Base;
using NyaFs.ImageFormat.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NyaFs.Processor.Scripting
{
    public class ScriptPlugins
    {
        private List<NyaPlugin> LoadedPlugins = new List<NyaPlugin>();

        private List<CompositeImagePlugin> Images = new List<CompositeImagePlugin>();
        private List<CompressorPlugin> Compressors = new List<CompressorPlugin>();
        private List<FilesystemPlugin> Filesystems = new List<FilesystemPlugin>();
        private List<KernelPlugin> Kernels = new List<KernelPlugin>();
        private List<DevtreePlugin> Dtbs = new List<DevtreePlugin>();
        private List<ServicePlugin> Services = new List<ServicePlugin>();
        private List<Plugins.CommandPlugin> Commands = new List<Plugins.CommandPlugin>();

        /// <summary>
        /// Get composite image plugins
        /// </summary>
        /// <returns></returns>
        public CompositeImagePlugin[] GetImages() => Images.ToArray();

        /// <summary>
        /// Get all available kernel images
        /// </summary>
        /// <returns></returns>
        public KernelPlugin[] GetKernels() => Kernels.ToArray();

        /// <summary>
        /// Get all available filesystems
        /// </summary>
        /// <returns></returns>
        public FilesystemPlugin[] GetFilesystems() => Filesystems.ToArray();

        /// <summary>
        /// Get all available device tree plugins
        /// </summary>
        /// <returns></returns>
        public DevtreePlugin[] GetDtbs() => Dtbs.ToArray();

        /// <summary>
        /// Get all available compressors
        /// </summary>
        /// <returns></returns>
        public CompressorPlugin[] GetCompressors() => Compressors.ToArray();

        /// <summary>
        /// Detect compression type of the data by asking every registered compressor plugin.
        /// </summary>
        public CompressionType DetectCompression(ImageData Data)
        {
            foreach (var C in Compressors)
            {
                if (C.DetectCompressor(Data))
                    return C.Compression;
            }

            return CompressionType.IH_COMP_NONE;
        }

        /// <summary>
        /// Find the composite container plugin that matches the data (or null).
        /// </summary>
        public CompositeImagePlugin DetectComposite(ImageData Data)
        {
            foreach (var I in Images)
            {
                if (I.DetectImage(Data))
                    return I;
            }

            return null;
        }

        /// <summary>
        /// Find the compressor plugin that matches the data (or null).
        /// </summary>
        public CompressorPlugin DetectCompressor(ImageData Data)
        {
            foreach (var C in Compressors)
            {
                if (C.DetectCompressor(Data))
                    return C;
            }

            return null;
        }

        /// <summary>
        /// Detect image element kind/format by asking registered kernel/filesystem plugins.
        /// (Composite containers are probed separately by the loading pipeline.)
        /// </summary>
        public DetectedImage DetectImage(ImageData Data)
        {
            foreach (var K in Kernels)
            {
                if (K.DetectKernel(Data))
                    return new DetectedImage(K.Kind, K.Format);
            }

            foreach (var F in Filesystems)
            {
                if (F.DetectFilesystem(Data))
                    return new DetectedImage(F.Kind, F.Format);
            }

            foreach (var D in Dtbs)
            {
                if (D.DetectDevtree(Data))
                    return new DetectedImage(D.Kind, D.Format);
            }

            return DetectedImage.Unknown;
        }

        /// <summary>
        /// Get compressor plugin by name
        /// </summary>
        /// <param name="Name"></param>
        /// <returns></returns>
        public CompressorPlugin GetCompressor(string Name)
        {
            foreach (var S in Compressors)
            {
                if (S.Name == Name)
                    return S;
            }

            return null;
        }

        /// <summary>
        /// Get kernel plugin by name
        /// </summary>
        /// <param name="Name"></param>
        /// <returns></returns>
        public KernelPlugin GetKernel(string Name)
        {
            foreach (var S in Kernels)
            {
                if (S.Name == Name)
                    return S;
            }

            return null;
        }

        /// <summary>
        /// Get filesystem plugin by name
        /// </summary>
        /// <param name="Name"></param>
        /// <returns></returns>
        public FilesystemPlugin GetFilesystem(string Name)
        {
            foreach (var S in Filesystems)
            {
                if (S.Name == Name)
                    return S;
            }

            return null;
        }

        /// <summary>
        /// Find a filesystem plugin by its format name (e.g. "cpio", "squashfs", "littlefs").
        /// </summary>
        public FilesystemPlugin GetFilesystemByFormat(string Format)
        {
            foreach (var S in Filesystems)
            {
                if (S.Format == Format)
                    return S;
            }

            return null;
        }

        /// <summary>
        /// Find a kernel plugin by its format name (e.g. "raw", "zimage").
        /// </summary>
        public KernelPlugin GetKernelByFormat(string Format)
        {
            foreach (var S in Kernels)
            {
                if (S.Format == Format)
                    return S;
            }

            return null;
        }

        /// <summary>
        /// Find a device tree plugin by its format name (e.g. "dtb").
        /// </summary>
        public DevtreePlugin GetDtbByFormat(string Format)
        {
            foreach (var S in Dtbs)
            {
                if (S.Format == Format)
                    return S;
            }

            return null;
        }

        /// <summary>
        /// Get image plugin by name
        /// </summary>
        /// <param name="Name"></param>
        /// <returns></returns>
        public CompositeImagePlugin GetImage(string Name)
        {
            foreach (var S in Images)
            {
                if (S.Name == Name)
                    return S;
            }

            return null;
        }

        /// <summary>
        /// Get service plugin by name
        /// </summary>
        /// <param name="Name"></param>
        /// <returns></returns>
        public ServicePlugin GetService(string Name)
        {
            foreach (var S in Services)
            {
                if (S.Name == Name)
                    return S;
            }

            return null;
        }

        private bool IsLoaded(string Name)
        {
            foreach(var P in LoadedPlugins)
            {
                if (P.Name == Name)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Insert a plugin keeping the list ordered by descending <see cref="NyaPlugin.Priority"/>
        /// (stable: equal priorities keep their registration order).
        /// </summary>
        private static void Insert<T>(List<T> List, T Plugin) where T : NyaPlugin
        {
            int i = 0;
            while ((i < List.Count) && (List[i].Priority >= Plugin.Priority)) i++;
            List.Insert(i, Plugin);
        }

        private bool AddPlugin(NyaPlugin Plugin)
        {
            var Compressor = Plugin as CompressorPlugin;
            if (Compressor != null)
            {
                Insert(Compressors, Compressor);
                return true;
            }

            var Kernel = Plugin as KernelPlugin;
            if (Kernel != null)
            {
                Insert(Kernels, Kernel);
                return true;
            }

            var Dtb = Plugin as DevtreePlugin;
            if (Dtb != null)
            {
                Insert(Dtbs, Dtb);
                return true;
            }


            var Filesystem = Plugin as FilesystemPlugin;
            if (Filesystem != null)
            {
                Insert(Filesystems, Filesystem);
                return true;
            }

            var Service = Plugin as ServicePlugin;
            if (Service != null)
            {
                Insert(Services, Service);
                return true;
            }

            var Command = Plugin as Plugins.CommandPlugin;
            if (Command != null)
            {
                Insert(Commands, Command);
                return true;
            }

            var Image = Plugin as CompositeImagePlugin;
            if (Image != null)
            {
                Insert(Images, Image);
                return true;
            }

            return false;
        }

        public bool Load(NyaPlugin Plugin)
        {
            if(IsLoaded(Plugin.Name))
                Log.Error(0, $"Plugin with name {Plugin.Name} already loaded");
            else
            {
                if(CheckPlugin(Plugin))
                {
                    if (AddPlugin(Plugin))
                    {
                        LoadedPlugins.Add(Plugin);
                        Log.Ok(3, $"Plugin {Plugin.Name} was loaded.");

                        return true;
                    }
                    else
                        Log.Error(0, $"Plugin {Plugin.Name} has unsupported type.");
                }
                else
                    Log.Error(0, $"Plugin {Plugin.Name} cannot be loaded.");
            }

            return false;
        }

        private bool CheckPluginType(Type type)
        {
            if (type.IsInterface || type.IsAbstract) return false;
            if (type.BaseType == null)
                return false;
            else
            {
                if (type.BaseType.FullName == "NyaFs.ImageFormat.Plugins.Base.NyaPlugin")
                    return true;
                else
                    return CheckPluginType(type.BaseType);
            }
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
                var Types = PluginAssembly.GetTypes().Where(t => CheckPluginType(t)).ToArray();
                foreach (var t in Types)
                {
                    var obj = AppDomain.CurrentDomain.CreateInstanceFrom(Filename, t.FullName).Unwrap();
                    if (obj != null)
                    {
                        var Plugin = obj as NyaPlugin;
                        if (Plugin != null)
                            Res.Add(Plugin);
                    }
                }
            }
            return Res.ToArray();
#endif
        }

        private bool CheckPlugin(NyaPlugin Plugin)
        {
            return true;
        }
    }
}
