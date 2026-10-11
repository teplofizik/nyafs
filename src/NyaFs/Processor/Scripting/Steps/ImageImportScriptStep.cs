using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Steps
{
    public class ImageImportScriptStep : ScriptStep
    {
        public ImageImportScriptStep(string Name) : base(Name)
        {

        }

        protected NyaFs.ImageFormat.Types.CPU FindExistArch(NyaFs.ImageFormat.BaseImageBlob Blob)
        {
            NyaFs.ImageFormat.Types.CPU arch = NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID;
            if (Blob.IsProvidedKernel)
            {
                var K = Blob.GetKernel();
                if (K.Info.Architecture != NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
                    arch = K.Info.Architecture;
            }
            if (Blob.IsProvidedFs)
            {
                var Fs = Blob.GetFilesystem();
                if (Fs.Info.Architecture != NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
                {
                    if (arch != NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
                    {
                        // Is detected os info differs to kernel
                        if (arch != Fs.Info.Architecture)
                            return NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID;
                    }
                    else
                        arch = Fs.Info.Architecture;
                }
            }
            if (Blob.IsProvidedDTB)
            {
                var Dtb = Blob.GetDevTree();
                if (Dtb.Info.Architecture != NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
                {
                    if (arch != NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
                    {
                        // Is detected os info differs to dtb
                        if (arch != Dtb.Info.Architecture)
                            return NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID;
                    }
                    else
                        arch = Dtb.Info.Architecture;
                }
            }

            return arch;
        }

        protected NyaFs.ImageFormat.Types.OS FindExistOS(NyaFs.ImageFormat.BaseImageBlob Blob)
        {
            NyaFs.ImageFormat.Types.OS os = NyaFs.ImageFormat.Types.OS.IH_OS_INVALID;
            if (Blob.IsProvidedKernel)
            {
                var K = Blob.GetKernel();
                if (K.Info.OperatingSystem != NyaFs.ImageFormat.Types.OS.IH_OS_INVALID)
                    os = K.Info.OperatingSystem;
            }
            if (Blob.IsProvidedFs)
            {
                var Fs = Blob.GetFilesystem();
                if (Fs.Info.OperatingSystem != NyaFs.ImageFormat.Types.OS.IH_OS_INVALID)
                {
                    if (os != NyaFs.ImageFormat.Types.OS.IH_OS_INVALID)
                    {
                        // Is FS os info differs to kernel
                        if (os != Fs.Info.OperatingSystem)
                            return NyaFs.ImageFormat.Types.OS.IH_OS_INVALID;
                    }
                    else
                        os = Fs.Info.OperatingSystem;
                }
            }

            return os;
        }
        protected void AssumeAutoImageParams(ImageProcessor Processor)
        {
            var Blob = Processor.GetBlob();
            var Arch = FindExistArch(Blob);
            if (Arch != NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
            {
                var K = Processor.GetKernel();
                if (K != null)
                {
                    if (K.Info.Architecture == NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
                        K.Info.Architecture = Arch;
                }
                var Fs = Processor.GetFs();
                if (Fs != null)
                {
                    if (Fs.Info.Architecture == NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
                        Fs.Info.Architecture = Arch;
                }
                var Dtb = Processor.GetDevTree();
                if (Dtb != null)
                {
                    if (Dtb.Info.Architecture == NyaFs.ImageFormat.Types.CPU.IH_ARCH_INVALID)
                        Dtb.Info.Architecture = Arch;
                }
            }
            var Os = FindExistOS(Blob);
            if (Os != NyaFs.ImageFormat.Types.OS.IH_OS_INVALID)
            {
                var K = Processor.GetKernel();
                if (K != null)
                {
                    if (K.Info.OperatingSystem == NyaFs.ImageFormat.Types.OS.IH_OS_INVALID)
                        K.Info.OperatingSystem = Os;
                }
                var Fs = Processor.GetFs();
                if (Fs != null)
                {
                    if (Fs.Info.OperatingSystem == NyaFs.ImageFormat.Types.OS.IH_OS_INVALID)
                        Fs.Info.OperatingSystem = Os;
                }
            }
        }

        protected void AssumeAutoKernelParams(ImageFormat.Elements.Kernel.LinuxKernel Kernel)
        {
            if (Kernel.Info.Architecture == ImageFormat.Types.CPU.IH_ARCH_INVALID)
            {
                var Type = Helper.KernelHelper.DetectImageArch(Kernel.Image);

                if (Type != ImageFormat.Types.CPU.IH_ARCH_INVALID)
                {
                    Log.Write(0, $"Assumed kernel architecture: {ImageFormat.Helper.FitHelper.GetCPUArchitecture(Type)}");
                    Kernel.Info.Architecture = Type;
                }
            }

            if (Kernel.Info.OperatingSystem == ImageFormat.Types.OS.IH_OS_INVALID)
            {
                var Os = Helper.KernelHelper.DetectImageOs(Kernel.Image);

                if (Os != ImageFormat.Types.OS.IH_OS_INVALID)
                {
                    Log.Write(0, $"Assumed kernel OS: {ImageFormat.Helper.FitHelper.GetOperatingSystem(Os)}");
                    Kernel.Info.OperatingSystem = Os;
                }
            }
        }

    }
}
