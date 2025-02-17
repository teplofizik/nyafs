﻿using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Kernel.Reader
{
    public class AndroidReader : Reader
    {
        Types.Android.LegacyAndroidImage Image;

        public AndroidReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }

        public AndroidReader(byte[] Raw)
        {
            Image = new Types.Android.LegacyAndroidImage(Raw);
        }

        public override void ReadToKernel(LinuxKernel Dst)
        {
            if(Image.IsMagicCorrect)
            {
                var Version = Image.HeaderVersion;
                if (Version < 3)
                {
                    ReadToKernelv0(Dst);
                }
                else
                {
                    // Different image format!..
                    throw new NotImplementedException("Android image v3-4 are not supported now!");
                }
            }
        }

        private void ReadToKernelv0(LinuxKernel Dst)
        {
            // TODO: detect image format...
            Dst.Info.Type = Types.ImageType.IH_TYPE_KERNEL;
            Dst.Info.OperatingSystem = Types.OS.IH_OS_LINUX;
            Dst.Info.Compression = Types.CompressionType.IH_COMP_NONE;
            Dst.Info.DataLoadAddress = Image.KernelAddress;
            Dst.Info.EntryPointAddress = Image.KernelAddress;
            Dst.Image = Image.Kernel;
        }
    }
}
