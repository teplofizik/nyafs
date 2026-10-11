using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Composite
{
    public class AndroidImageWriter
    {
        private readonly string Path;

        private bool KernelAvailable = false;
        private bool FsAvailable = false;

        public AndroidImageWriter(string Path)
        {
            this.Path = Path;
        }

        public bool Write(BaseImageBlob Blob)
        {
            var Kernel = Blob.GetKernel();
            var Fs = Blob.GetFilesystem();


            if (!CheckFs(Blob.GetFilesystem(0)))
                Log.Warning(0, "No filesystem!");
            else
                FsAvailable = true;

            if (!CheckKernel(Blob.GetKernel(0)))
                Log.Warning(0, "No kernel!");
            else
                KernelAvailable = true;


            return false;
        }

        private bool CheckFs(Elements.Fs.LinuxFilesystem Fs)
        {
            if (Fs == null)
                return false;

            return true;
        }

        private bool CheckKernel(Elements.Kernel.LinuxKernel Kernel)
        {
            if (Kernel == null)
                return false;

            return true;
        }
    }
}
