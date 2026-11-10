using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Plugins.Base;
using NyaFs.ImageFormat.Types;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NyaFs.ImageFormat.Plugins
{
    /// <summary>
    /// Process-wide plugin registry. It contains ordered plugin snapshots and the
    /// detection helpers shared by the processor, pipeline and legacy facades.
    /// </summary>
    public class PluginRegistry
    {
        private readonly List<NyaPlugin> LoadedPlugins = new List<NyaPlugin>();
        private readonly List<CompositeImagePlugin> Images = new List<CompositeImagePlugin>();
        private readonly List<CompressorPlugin> Compressors = new List<CompressorPlugin>();
        private readonly List<FilesystemPlugin> Filesystems = new List<FilesystemPlugin>();
        private readonly List<KernelPlugin> Kernels = new List<KernelPlugin>();
        private readonly List<DevtreePlugin> Dtbs = new List<DevtreePlugin>();
        private readonly List<ServicePlugin> Services = new List<ServicePlugin>();
        private readonly List<RemoteFilesystemPlugin> Remotes = new List<RemoteFilesystemPlugin>();

        public CompositeImagePlugin[] GetImages() => Images.ToArray();
        public KernelPlugin[] GetKernels() => Kernels.ToArray();
        public FilesystemPlugin[] GetFilesystems() => Filesystems.ToArray();
        public DevtreePlugin[] GetDtbs() => Dtbs.ToArray();
        public CompressorPlugin[] GetCompressors() => Compressors.ToArray();
        public RemoteFilesystemPlugin[] GetRemotes() => Remotes.ToArray();
        public ServicePlugin[] GetServices() => Services.ToArray();

        public CompressionType DetectCompression(ImageData Data)
        {
            foreach (var C in Compressors)
                if (C.DetectCompressor(Data))
                    return C.Compression;

            return CompressionType.IH_COMP_NONE;
        }

        public CompositeImagePlugin DetectComposite(ImageData Data)
        {
            foreach (var I in Images)
                if (I.DetectImage(Data))
                    return I;

            return null;
        }

        public CompressorPlugin DetectCompressor(ImageData Data)
        {
            foreach (var C in Compressors)
                if (C.DetectCompressor(Data))
                    return C;

            return null;
        }

        public DetectedImage DetectImage(ImageData Data)
        {
            foreach (var K in Kernels)
                if (K.DetectKernel(Data))
                    return new DetectedImage(K.Kind, K.Format);

            foreach (var F in Filesystems)
                if (F.DetectFilesystem(Data))
                    return new DetectedImage(F.Kind, F.Format);

            foreach (var D in Dtbs)
                if (D.DetectDevtree(Data))
                    return new DetectedImage(D.Kind, D.Format);

            return DetectedImage.Unknown;
        }

        public FilesystemPlugin DetectFilesystem(ImageData Data)
        {
            var Ordered = Filesystems.OrderByDescending(F => F.Priority).ToList();

            foreach (var F in Ordered)
            {
                try
                {
                    if (F.DetectFilesystem(Data))
                        return F;
                }
                catch
                {
                    // A malformed image must not break auto-detection.
                }
            }

            return null;
        }

        public CompressorPlugin GetCompressor(string Name) =>
            Compressors.Find(S => S.Name == Name);

        public KernelPlugin GetKernel(string Name) =>
            Kernels.Find(S => S.Name == Name);

        public FilesystemPlugin GetFilesystem(string Name) =>
            Filesystems.Find(S => S.Name == Name);

        public FilesystemPlugin GetFilesystemByFormat(string Format) =>
            Filesystems.Find(S => S.Format == Format);

        /// <summary>
        /// Resolve a filesystem by its canonical format or plugin name.
        /// External plugins use the same path as built-ins.
        /// </summary>
        public FilesystemPlugin ResolveFilesystem(string NameOrFormat)
        {
            if (string.IsNullOrEmpty(NameOrFormat))
                return null;

            return Filesystems.Find(S => string.Equals(S.Format, NameOrFormat, StringComparison.OrdinalIgnoreCase))
                ?? Filesystems.Find(S => string.Equals(S.Name, NameOrFormat, StringComparison.OrdinalIgnoreCase))
                ?? Filesystems.Find(S => S.Aliases.Any(A => string.Equals(A, NameOrFormat, StringComparison.OrdinalIgnoreCase)));
        }

        public KernelPlugin ResolveKernel(string NameOrFormat)
        {
            if (string.IsNullOrEmpty(NameOrFormat))
                return null;

            return Kernels.Find(S => string.Equals(S.Format, NameOrFormat, StringComparison.OrdinalIgnoreCase))
                ?? Kernels.Find(S => string.Equals(S.Name, NameOrFormat, StringComparison.OrdinalIgnoreCase))
                ?? Kernels.Find(S => S.Aliases.Any(A => string.Equals(A, NameOrFormat, StringComparison.OrdinalIgnoreCase)));
        }

        public DevtreePlugin ResolveDtb(string NameOrFormat)
        {
            if (string.IsNullOrEmpty(NameOrFormat))
                return null;

            return Dtbs.Find(S => string.Equals(S.Format, NameOrFormat, StringComparison.OrdinalIgnoreCase))
                ?? Dtbs.Find(S => string.Equals(S.Name, NameOrFormat, StringComparison.OrdinalIgnoreCase))
                ?? Dtbs.Find(S => S.Aliases.Any(A => string.Equals(A, NameOrFormat, StringComparison.OrdinalIgnoreCase)));
        }

        public KernelPlugin GetKernelByFormat(string Format) =>
            ResolveKernel(Format);

        public DevtreePlugin GetDtbByFormat(string Format) =>
            ResolveDtb(Format);

        public CompositeImagePlugin ResolveImage(string NameOrFormat)
        {
            if (string.IsNullOrEmpty(NameOrFormat))
                return null;

            return Images.Find(S => string.Equals(S.Format, NameOrFormat, StringComparison.OrdinalIgnoreCase))
                ?? Images.Find(S => string.Equals(S.Name, NameOrFormat, StringComparison.OrdinalIgnoreCase))
                ?? Images.Find(S => S.Aliases.Any(A => string.Equals(A, NameOrFormat, StringComparison.OrdinalIgnoreCase)));
        }

        public CompositeImagePlugin GetImage(string Name) =>
            ResolveImage(Name);

        public RemoteFilesystemPlugin GetRemote(string Protocol)
        {
            if (string.IsNullOrEmpty(Protocol))
                return null;

            foreach (var R in Remotes)
            {
                if (R.Protocols == null)
                    continue;

                foreach (var P in R.Protocols)
                    if (string.Equals(P, Protocol, StringComparison.OrdinalIgnoreCase))
                        return R;
            }

            return null;
        }

        public ServicePlugin GetService(string Name) =>
            Services.Find(S => S.Name == Name);

        public bool IsLoaded(string Name) =>
            LoadedPlugins.Exists(P => P.Name == Name);

        private static void Insert<T>(List<T> List, T Plugin) where T : NyaPlugin
        {
            int i = 0;
            while ((i < List.Count) && (List[i].Priority >= Plugin.Priority)) i++;
            List.Insert(i, Plugin);
        }

        private bool AddPlugin(NyaPlugin Plugin)
        {
            var Compressor = Plugin as CompressorPlugin;
            if (Compressor != null) { Insert(Compressors, Compressor); return true; }

            var Kernel = Plugin as KernelPlugin;
            if (Kernel != null) { Insert(Kernels, Kernel); return true; }

            var Dtb = Plugin as DevtreePlugin;
            if (Dtb != null) { Insert(Dtbs, Dtb); return true; }

            var Filesystem = Plugin as FilesystemPlugin;
            if (Filesystem != null) { Insert(Filesystems, Filesystem); return true; }

            var Service = Plugin as ServicePlugin;
            if (Service != null) { Insert(Services, Service); return true; }

            var Remote = Plugin as RemoteFilesystemPlugin;
            if (Remote != null) { Insert(Remotes, Remote); return true; }

            var Image = Plugin as CompositeImagePlugin;
            if (Image != null) { Insert(Images, Image); return true; }

            // Command plugins are processor-facing; keep them in LoadedPlugins only.
            return Plugin.PluginType == "command";
        }

        public bool Load(NyaPlugin Plugin)
        {
            if (IsLoaded(Plugin.Name))
            {
                Log.Error(0, $"Plugin with name {Plugin.Name} already loaded");
                return false;
            }

            if (!AddPlugin(Plugin))
            {
                Log.Error(0, $"Plugin {Plugin.Name} has unsupported type.");
                return false;
            }

            LoadedPlugins.Add(Plugin);
            Log.Ok(3, $"Plugin {Plugin.Name} was loaded.");
            return true;
        }
    }
}
