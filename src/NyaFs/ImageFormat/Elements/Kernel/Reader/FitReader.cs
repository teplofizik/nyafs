using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Elements.Kernel.Reader
{
    public class FitReader : Reader
    {
        bool Loaded = false;

        FlattenedDeviceTree.FlattenedDeviceTree Fit;

        FlattenedDeviceTree.Types.Node KernelNode = null;

        // https://github.com/siemens/u-boot/blob/master/common/image.c

        public FitReader(string Filename, string Config) : this(System.IO.File.ReadAllBytes(Filename), Filename, Config)
        {
        }

        public FitReader(byte[] Raw, string Filename, string Config)
        {
            Fit = new FlattenedDeviceTree.Reader.FDTReader(Raw).Read();

            if (Fit.Root.Nodes.Count == 0)
            {
                Log.Error(0, $"Could not load FIT image from file {Filename}");
                return;
            }

            var Configurations = Fit.Get("configurations");
            if (Configurations == null)
            {
                Log.Error(0, $"Invalid FIT image {Filename}: no 'configuration' node.");
                return;
            }
            var DefaultConfig = Helper.FitHelper.GetDefaultConfigNode(Configurations, Config);

            if (DefaultConfig == null)
            {
                Log.Error(0, $"Invalid FIT image {Filename}: no configurations found in 'configurations' node.");
                return;
            }

            var Configuration = Fit.Get("configurations/" + DefaultConfig);

            if (Configuration == null)
            {
                Log.Error(0, $"Invalid FIT image {Filename}: no 'configuration/{DefaultConfig}' node.");
                return;
            }

            // kernel fdt ramdisk
            var KernelNodeName = Configuration.GetStringValue("kernel");
            if (KernelNodeName == null)
            {
                Log.Error(0, $"Invalid FIT image {Filename}: no 'kernel' parameter in 'configuration/{DefaultConfig}' node.");
            }
            else
            {
                KernelNode = Fit.Get($"images/{KernelNodeName}");
                if (KernelNode == null)
                {
                    Log.Error(0, $"Invalid FIT image {Filename}: no '{KernelNodeName}' node.");
                    return;
                }
                Loaded = true;
            }
        }

        /// <summary>
        /// Читаем в дерево устройств из внешнего источника
        /// </summary>
        /// <param name="Dst"></param>
        public override void ReadToKernel(LinuxKernel Dst)
        {
            if (!Loaded) return;

            var ImgType = KernelNode.GetStringValue("type");
            if (ImgType == null)
            {
                Log.Error(0, $"Invalid FIT image: no 'type' parameter in loaded ramdisk node.");
                return;
            }


            var Os = KernelNode.GetStringValue("os");
            if (Os == null)
            {
                Log.Error(0, $"Invalid FIT image: no 'os' parameter in loaded ramdisk node.");
                return;
            }

            var Arch = KernelNode.GetStringValue("arch");
            if (Arch == null)
            {
                Log.Error(0, $"Invalid FIT image: no 'arch' parameter in loaded ramdisk node.");
                return;
            }

            var Compression = KernelNode.GetStringValue("compression");
            if (Compression == null)
            {
                Log.Error(0, $"Invalid FIT image: no 'compression' parameter in loaded ramdisk node.");
                return;
            }

            var Load = KernelNode.GetValue("load");
            var Entry = KernelNode.GetValue("entry");

            UInt64 EntryAddress = (Entry == null) ? 0 : (Entry.Length == 8) ? Entry.ReadUInt64BE(0) : Entry.ReadUInt32BE(0);
            UInt64 LoadAddress = (Load == null) ? 0 : (Load.Length == 8) ? Load.ReadUInt64BE(0) : Load.ReadUInt32BE(0);

            var Data = KernelNode.GetValue("data");
            if (Data == null)
            {
                Log.Error(0, $"Invalid FIT image: no 'data' parameter in kernel node.");
                return;
            }

            var HashNode = Helper.FitHelper.GetHashNode(KernelNode);
            if (HashNode != null)
            {
                if (!Helper.FitHelper.CheckHash(Data, HashNode))
                {
                    Log.Error(0, $"Invalid FIT image: hash is not equal.");
                    return;
                }
            }
            else
                Log.Warning(0, $"No hash node in kernel image node!");

            var Kernel = Helper.FitHelper.GetDecompressedData(Data, Compression);
            Dst.Info.Architecture = Helper.FitHelper.GetCPUArchitecture(Arch);
            Dst.Info.Type = Helper.FitHelper.GetType(ImgType);
            Dst.Info.OperatingSystem = Helper.FitHelper.GetOperatingSystem(Os);
            Dst.Info.DataLoadAddress = LoadAddress;
            Dst.Info.EntryPointAddress = EntryAddress;
            Dst.Info.Compression = Helper.FitHelper.GetCompression(Compression);
            Dst.Image = Kernel;
        }

    }
}
