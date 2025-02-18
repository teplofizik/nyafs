﻿using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Elements.Fs.Reader
{
    public class LegacyReader : Reader
    {
        bool Loaded = false;
        Types.LegacyImage Image;

        public LegacyReader(byte[] Raw)
        {
            Image = new Types.LegacyImage(Raw);

            Loaded = CheckImage();
        }

        public LegacyReader(string Filename)
        {
            Image = new Types.LegacyImage(Filename);

            Loaded = CheckImage();
        }

        private bool CheckImage()
        {
            if (!Image.CorrectHeader)
            {
                Log.Error(0, $"Invalid legacy header in file.");
                return false;
            }
            if (!Image.Correct)
            {
                Log.Error(0, $"Invalid legacy image.");
                return false;
            }

            if (Image.Type != Types.ImageType.IH_TYPE_RAMDISK)
            {
                Log.Error(0, $"File is not ramdisk legacy file.");
                return false;
            }

            return true;
        }

        public void UpdateImageInfo(LinuxFilesystem Dst)
        {
            if (Loaded)
            {
                Dst.Info.Architecture = Image.CPUArchitecture;
                Dst.Info.OperatingSystem = Image.OperatingSystem;
                Dst.Info.Name = Image.Name;
                Dst.Info.DataLoadAddress = Image.DataLoadAddress;
                Dst.Info.EntryPointAddress = Image.EntryPointAddress;
                Dst.Info.Type = Image.Type;

                Dst.Info.Compression = Image.Compression;
            }
        }

        /// <summary>
        /// Читаем в файловую систему из cpio-файла
        /// </summary>
        /// <param name="Dst"></param>
        public override void ReadToFs(LinuxFilesystem Dst)
        {
            if (!Loaded) return;

            var Data = GetDecompressedData(Image.ImageData, Image.Compression);

            if (DetectAndRead(Dst, Data))
                UpdateImageInfo(Dst);
            else
            {
                if(Image.Compression == Types.CompressionType.IH_COMP_NONE)
                {
                    var Comp = Helper.FitHelper.DetectCompression(Image.ImageData);
                    if (Comp != Types.CompressionType.IH_COMP_NONE)
                    {
                        byte[] Decompressed = Helper.FitHelper.GetDecompressedData(Image.ImageData, Comp);

                        Log.Warning(0, $"Invalid compression type NONE in legacy header but detected {Comp}");
                        if (DetectAndRead(Dst, Decompressed))
                            UpdateImageInfo(Dst);
                        else
                            Log.Error(0, "Invalid BMU archive: unknown ramfs format");
                    }
                }
                else
                    Log.Error(0, "Unsupported filesystem...");
            }
        }

        byte[] GetDecompressedData(byte[] Source, Types.CompressionType Compression)
        {
            switch(Compression)
            {
                case Types.CompressionType.IH_COMP_GZIP: return Compressors.Gzip.Decompress(Source);
                case Types.CompressionType.IH_COMP_LZMA: return Compressors.Lzma.Decompress(Source);
                case Types.CompressionType.IH_COMP_NONE: return Source;
                default:
                    Log.Error(0, $"Unsupported compression type: {Compression}");
                    throw new ArgumentException($"Unsupported compression type: {Compression}");
            }
        }

        /// <summary>
        /// Тип ОС
        /// </summary>
        /// <param name="OS"></param>
        /// <returns></returns>
        private string GetOS(Types.OS OS)
        {
            switch(OS)
            {
                case Types.OS.IH_OS_LINUX: return "Linux";
                default: return $"{OS}";
            }
        }

        /// <summary>
        /// Тип архитектуры
        /// </summary>
        /// <param name="CPU"></param>
        /// <returns></returns>
        private string GetArch(Types.CPU CPU)
        {
            switch (CPU)
            {
                case Types.CPU.IH_ARCH_ARM: return "ARM";
                case Types.CPU.IH_ARCH_ARM64: return "ARM64";
                case Types.CPU.IH_ARCH_I386: return "I386";
                case Types.CPU.IH_ARCH_MIPS: return "MIPS";
                case Types.CPU.IH_ARCH_MIPS64: return "MIPS64";
                case Types.CPU.IH_ARCH_X86_64: return "X86_64";
                default: return $"{CPU}";
            }
        }

        /// <summary>
        /// Тип сжатия
        /// </summary>
        /// <param name="Compr"></param>
        /// <returns></returns>
        private string GetCompression(Types.CompressionType Compr)
        {
            switch (Compr)
            {
                case Types.CompressionType.IH_COMP_GZIP: return "gzip";
                case Types.CompressionType.IH_COMP_NONE: return "none";
                default: return $"{Compr}";
            }
        }

        private string GetType(Types.ImageType Type)
        {
            switch (Type)
            {
                case ImageFormat.Types.ImageType.IH_TYPE_KERNEL: return "kernel";
                case ImageFormat.Types.ImageType.IH_TYPE_MULTI: return "multi";
                case ImageFormat.Types.ImageType.IH_TYPE_SCRIPT: return "script";
                case ImageFormat.Types.ImageType.IH_TYPE_RAMDISK: return "ramdisk";
                default: return $"{Type}";
            }
        }
    }
}
