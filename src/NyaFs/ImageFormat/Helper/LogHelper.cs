﻿using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Helper
{
    public static class LogHelper
    {
        public static void KernelInfo(Elements.Kernel.LinuxKernel Kernel)
        {
            Log.Ok(1, "Kernel:");
            Log.Write(1, $"  Operating System: {FitHelper.GetOperatingSystem(Kernel.Info.OperatingSystem)}");
            Log.Write(1, $"      Architecture: {FitHelper.GetCPUArchitecture(Kernel.Info.Architecture)}");
            Log.Write(1, $"       Compression: {FitHelper.GetCompression(Kernel.Info.Compression)}");
            Log.Write(1, $"              Type: {FitHelper.GetType(Kernel.Info.Type)}");
            Log.Write(1, $"      Load address: {Kernel.Info.DataLoadAddress:x08}");
            Log.Write(1, $"     Entry address: {Kernel.Info.EntryPointAddress:x08}");
        }

        public static void RamfsInfo(Elements.Fs.LinuxFilesystem Fs)
        {
            string FsType = FitHelper.GetFilesystemType(Fs.FilesystemType);
            Log.Ok(1, "Filesystem:");
            Log.Write(1, $"    Operating System: {FitHelper.GetOperatingSystem(Fs.Info.OperatingSystem)}");
            Log.Write(1, $"        Architecture: {FitHelper.GetCPUArchitecture(Fs.Info.Architecture)}");
            Log.Write(1, $"         Compression: {FitHelper.GetCompression(Fs.Info.Compression)}");
            Log.Write(1, $"                Type: {FitHelper.GetType(Fs.Info.Type)}");
            Log.Write(1, $"          Filesystem: {FsType}");
            if (Fs.FilesystemType == Types.FsType.SquashFs)
                Log.Write(1, $"Squashfs compression: {FitHelper.GetCompression(Fs.SquashFsCompression)}");
            
            Log.Write(1, $"        Content size: {Fs.GetContentSize()}");
        }

        public static void DevtreeInfo(Elements.Dtb.DeviceTree Dtb)
        {
            Log.Ok(1, "Device tree:");
            Log.Write(1, $"      Architecture: {FitHelper.GetCPUArchitecture(Dtb.Info.Architecture)}");
            Log.Write(1, $"       Compression: {FitHelper.GetCompression(Dtb.Info.Compression)}");
            Log.Write(1, $"              Type: {FitHelper.GetType(Dtb.Info.Type)}");
        }
    }
}
