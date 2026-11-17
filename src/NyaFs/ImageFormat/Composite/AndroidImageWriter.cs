using NyaFs.ImageFormat.Elements.Fs;
using NyaFs.ImageFormat.Elements.Kernel;
using NyaFs.ImageFormat.Helper;
using NyaFs.ImageFormat.Plugins;
using NyaFs.ImageFormat.Types.Android;
using System;
using System.IO;
using System.Text;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Android boot image writer (v0-v2). v3/v4 use a different layout.
    /// </summary>
    public class AndroidImageWriter
    {
        private readonly string Path;

        /// <summary>Registry used to resolve filesystem/kernel/dtb writers (defaults to the ambient one).</summary>
        public Plugins.PluginRegistry Registry { get; set; } = Plugins.PluginHost.Current;

        public AndroidImageWriter(string Path)
        {
            this.Path = Path;
        }

        public byte[] Build(BaseImageBlob Blob, out string Error)
        {
            Error = null;
            try
            {
                var Kernel = Blob.GetKernel(0);
                var Fs = Blob.GetFilesystem(0);
                var Dtb = Blob.GetDevTree(0);
                if (((Kernel == null) || !Kernel.Loaded) && ((Fs == null) || !Fs.Loaded))
                {
                    Error = "Android boot image requires a kernel or ramdisk";
                    return null;
                }

                var Version = (int)GetUInt(Blob, "header-version", 0);
                if ((Version < 0) || (Version > 4))
                    Version = 0;

                var PageSize = (int)GetUInt(Blob, "page-size", 2048);
                if (PageSize < 512)
                    PageSize = 2048;

                var KernelData = (Kernel != null && Kernel.Loaded) ? GetKernelData(Kernel) : Array.Empty<byte>();
                var RamdiskData = (Fs != null && Fs.Loaded) ? GetFilesystemData(Fs) : Array.Empty<byte>();
                var SecondData = GetPlanRaw(Blob, "second") ?? Array.Empty<byte>();
                var RecoveryData = Version >= 1 ? (GetPlanRaw(Blob, "recovery_dtbo") ?? Array.Empty<byte>()) : Array.Empty<byte>();
                var DtbData = Version >= 2 ? (GetDtbData(Dtb) ?? Array.Empty<byte>()) : Array.Empty<byte>();

                if (Version >= 3)
                {
                    var SignatureData = Version >= 4 ? (GetPlanRaw(Blob, "boot_signature") ?? Array.Empty<byte>()) : Array.Empty<byte>();
                    return BuildV3(Blob, KernelData, RamdiskData, Version, SignatureData);
                }

                var HeaderSize = Version == 2 ? 0x680 : (Version == 1 ? 0x670 : 0x800);
                var KernelOffset = Align(HeaderSize, PageSize);
                var RamdiskOffset = Align(KernelOffset + KernelData.Length, PageSize);
                var SecondOffset = Align(RamdiskOffset + RamdiskData.Length, PageSize);
                var RecoveryOffset = Align(SecondOffset + SecondData.Length, PageSize);
                var DtbOffset = Align(RecoveryOffset + RecoveryData.Length, PageSize);
                var End = DtbOffset + DtbData.Length;
                var Total = Align(End, PageSize);

                var Result = new byte[Total];
                SetString(Result, 0, 8, "ANDROID!");
                SetU32(Result, 0x08, (uint)KernelData.Length);
                SetU32(Result, 0x0C, (uint)GetUInt(Blob, "kernel-addr", Kernel?.Info.DataLoadAddress ?? 0));
                SetU32(Result, 0x10, (uint)RamdiskData.Length);
                SetU32(Result, 0x14, (uint)GetUInt(Blob, "ramdisk-addr", Fs?.Info.DataLoadAddress ?? 0));
                SetU32(Result, 0x18, (uint)SecondData.Length);
                SetU32(Result, 0x1C, (uint)GetUInt(Blob, "second-addr", 0));
                SetU32(Result, 0x20, (uint)GetUInt(Blob, "tags-addr", 0));
                SetU32(Result, 0x24, (uint)PageSize);
                SetU32(Result, 0x28, (uint)Version);
                SetU32(Result, 0x2C, (uint)GetUInt(Blob, "os-version", 0));
                SetString(Result, 0x30, 0x10, GetString(Blob, "name", ""));
                SetString(Result, 0x40, 0x200, GetString(Blob, "cmdline", ""));
                SetString(Result, 0x260, 0x400, GetString(Blob, "extra-cmdline", ""));

                if (Version >= 1)
                {
                    SetU32(Result, 0x660, (uint)RecoveryData.Length);
                    SetU64(Result, 0x664, GetUInt(Blob, "recovery-dtbo-addr", 0));
                    SetU32(Result, 0x66C, (uint)HeaderSize);
                }
                if (Version >= 2)
                {
                    SetU32(Result, 0x670, (uint)DtbData.Length);
                    SetU64(Result, 0x674, GetUInt(Blob, "dtb-addr", 0));
                }

                Copy(Result, KernelOffset, KernelData);
                Copy(Result, RamdiskOffset, RamdiskData);
                Copy(Result, SecondOffset, SecondData);
                if (Version >= 1)
                    Copy(Result, RecoveryOffset, RecoveryData);
                if (Version >= 2)
                    Copy(Result, DtbOffset, DtbData);

                return Result;
            }
            catch (Exception E)
            {
                Error = E.Message;
                Log.Error(0, $"Cannot write Android boot image: {E.Message}");
                return null;
            }
        }

        public bool Write(BaseImageBlob Blob)
        {
            var Data = Build(Blob, out var Error);
            if (Data == null)
                return false;
            File.WriteAllBytes(Path, Data);
            return true;
        }

        private byte[] BuildV3(BaseImageBlob Blob, byte[] KernelData, byte[] RamdiskData,
            int Version, byte[] SignatureData)
        {
            const int PageSize = 4096;
            var HeaderSize = (Version >= 4) ? AndroidImageV3.HeaderSizeV4 : AndroidImageV3.HeaderSizeV3;
            var KernelOffset = PageSize;
            var RamdiskOffset = Align(KernelOffset + KernelData.Length, PageSize);
            var SignatureOffset = Align(RamdiskOffset + RamdiskData.Length, PageSize);
            var SignatureSize = (Version >= 4) ? (SignatureData?.Length ?? 0) : 0;
            var Total = (Version >= 4)
                ? Align(SignatureOffset + SignatureSize, PageSize)
                : Align(RamdiskOffset + RamdiskData.Length, PageSize);
            var Result = new byte[Total];

            SetString(Result, 0, 8, "ANDROID!");
            SetU32(Result, 0x08, (uint)KernelData.Length);
            SetU32(Result, 0x0C, (uint)RamdiskData.Length);
            SetU32(Result, 0x10, (uint)GetUInt(Blob, "os-version", 0));
            SetU32(Result, 0x14, (uint)HeaderSize);
            SetU32(Result, 0x28, (uint)Version);

            var Cmdline = GetString(Blob, "cmdline", "");
            var Extra = GetString(Blob, "extra-cmdline", "");
            if (!String.IsNullOrEmpty(Extra))
                Cmdline = String.IsNullOrEmpty(Cmdline) ? Extra : (Cmdline + " " + Extra);
            SetString(Result, 0x2C, 0x600, Cmdline);

            if (Version >= 4)
                SetU32(Result, AndroidImageV3.SignatureSizeField, (uint)SignatureSize);

            Copy(Result, KernelOffset, KernelData);
            Copy(Result, RamdiskOffset, RamdiskData);
            if (Version >= 4)
                Copy(Result, SignatureOffset, SignatureData);
            return Result;
        }

        private byte[] GetKernelData(LinuxKernel Kernel)
        {
            if (!String.IsNullOrEmpty(Kernel.KernelFormat))
            {
                var Plugin = (Registry ?? PluginHost.Current).ResolveKernel(Kernel.KernelFormat);
                if ((Plugin != null) && Plugin.SupportWrite)
                {
                    var Writer = Plugin.GetKernelWriter(Kernel);
                    if (Writer != null)
                    {
                        Writer.WriteKernel(Kernel);
                        if (Writer.HasRawStreamData)
                            return Writer.RawStream;
                    }
                }
            }
            return Kernel.Image ?? Array.Empty<byte>();
        }

        private byte[] GetFilesystemData(LinuxFilesystem Fs)
        {
            Elements.Fs.Writer.Writer Writer = null;
            var Plugin = (Registry ?? PluginHost.Current).ResolveFilesystem(Fs.FilesystemFormat);
            if ((Plugin != null) && Plugin.SupportWrite)
                Writer = Plugin.GetFilesystemWriter(Fs);
            if (Writer == null)
                Writer = Elements.Fs.Writer.Writer.GetRawFilesystemWriter(Fs);

            Writer.WriteFs(Fs);
            if (!Writer.HasRawStreamData)
                return Array.Empty<byte>();

            return FitHelper.GetCompressedData(Writer.RawStream, Fs.Info.Compression);
        }

        private byte[] GetDtbData(Elements.Dtb.DeviceTree Dtb)
        {
            if (Dtb == null)
                return null;

            var Plugin = (Registry ?? PluginHost.Current).ResolveDtb(Dtb.DevtreeFormat);
            if ((Plugin != null) && Plugin.SupportWrite)
            {
                var Writer = Plugin.GetDevtreeWriter(Dtb);
                if (Writer != null)
                {
                    Writer.WriteDevtree(Dtb);
                    if (Writer.HasRawStreamData)
                        return Writer.RawStream;
                }
            }
            return new FlattenedDeviceTree.Writer.FDTWriter(Dtb.DevTree).GetBinary();
        }

        private static byte[] GetPlanRaw(BaseImageBlob Blob, string Name)
        {
            var Root = Blob.BuildPlan?.Root;
            if (Root == null)
                return null;

            foreach (var Child in Root.Children)
                if (Child.Name == Name && Child.Raw != null)
                    return Child.Raw;
            return null;
        }

        private static ulong GetUInt(BaseImageBlob Blob, string Name, ulong Default)
        {
            var Text = Blob.GetContainerOption("android", Name, null);
            if (String.IsNullOrEmpty(Text))
                return Default;
            ulong Value;
            if (Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return UInt64.TryParse(Text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out Value) ? Value : Default;
            return UInt64.TryParse(Text, out Value) ? Value : Default;
        }

        private static string GetString(BaseImageBlob Blob, string Name, string Default) =>
            Blob.GetContainerOption("android", Name, Default) ?? Default;

        private static void Copy(byte[] Data, int Offset, byte[] Value)
        {
            if (Value != null && Value.Length > 0)
                Buffer.BlockCopy(Value, 0, Data, Offset, Value.Length);
        }

        private static int Align(int Value, int Alignment) =>
            ((Value + Alignment - 1) / Alignment) * Alignment;

        private static void SetU32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }

        private static void SetU64(byte[] Data, int Offset, ulong Value)
        {
            SetU32(Data, Offset, (uint)Value);
            SetU32(Data, Offset + 4, (uint)(Value >> 32));
        }

        private static void SetString(byte[] Data, int Offset, int Size, string Value)
        {
            var Bytes = Encoding.ASCII.GetBytes(Value ?? "");
            Buffer.BlockCopy(Bytes, 0, Data, Offset, Math.Min(Size, Bytes.Length));
        }
    }
}