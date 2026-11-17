using System;
using System.Collections.Generic;
using NyaFs.Filesystem.F2fs;
using NyaFs.Filesystem.Jffs2;
using NyaFs.Filesystem.SquashFs.Types;
using NyaFs.Filesystem.UbiFs.Ubifs;
using NyaFs.ImageFormat.Elements.Fs;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Applies layout `options` to a filesystem object. Mirrors the `set ramfs ...`
    /// command names and also accepts unprefixed aliases when the format is known
    /// (e.g. `compression: xz` for a squashfs root).
    /// </summary>
    public static class FilesystemPlanOptions
    {
        public static bool Apply(LinuxFilesystem Fs, string Format,
            IEnumerable<KeyValuePair<string, string>> Options, PluginRegistry Plugins, out string Error)
        {
            Error = null;
            if (Fs == null)
            {
                Error = "No filesystem object";
                return false;
            }

            var Plugin = Plugins?.ResolveFilesystem(Format);
            if (Plugin == null)
            {
                Error = $"No filesystem plugin for '{Format}'";
                return false;
            }

            if (Options == null)
                return true;

            foreach (var Pair in Options)
            {
                if (String.IsNullOrWhiteSpace(Pair.Key) || (Pair.Value == null))
                    continue;

                if (Pair.Key.StartsWith("plan.", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (String.Equals(Pair.Key, "fill", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!ApplyOne(Fs, Format, Plugin, Pair.Key.Trim(), Pair.Value.Trim(), out Error))
                    return false;
            }

            return true;
        }

        private static bool ApplyOne(LinuxFilesystem Fs, string Format,
            NyaFs.ImageFormat.Plugins.Base.FilesystemPlugin Plugin,
            string Key, string Value, out string Error)
        {
            Error = null;
            var Name = Key.ToLowerInvariant();
            var Fmt = (Format ?? "").ToLowerInvariant();
            var Short = Name;

            if (!String.IsNullOrEmpty(Fmt) && Name.StartsWith(Fmt + "."))
                Short = Name.Substring(Fmt.Length + 1);

            switch (Name)
            {
                case "fs-size":
                case "fs_size":
                    return SetFsSize(Fs, Value, out Error);

                case "size":
                    Error = "Use node/slot 'size' for the region; use 'fs-size' for the filesystem volume";
                    return false;

                case "squashfs.compression":
                    return SetSquashFs(Fs, Value, out Error);

                case "ubifs.compression":
                    return SetUbifs(Fs, Value, out Error);

                case "f2fs.compression":
                    return SetF2fs(Fs, Value, out Error);

                case "f2fs.compression.cluster":
                case "f2fs.compression-cluster":
                    return SetF2fsCluster(Fs, Value, out Error);

                case "jffs2.compression":
                    return SetJffs2(Fs, Value, out Error);

                case "jffs2.pagesize":
                case "jffs2.page-size":
                    return SetJffs2Geometry(Fs, Value, true, out Error);

                case "jffs2.eraseblock":
                case "jffs2.erase-block":
                    return SetJffs2Geometry(Fs, Value, false, out Error);

                case "fat.type":
                    return SetFatType(Fs, Value, out Error);

                case "udf.vat":
                    return SetBool(Fs, Value, out var Vat, out Error) && SetUdfVat(Fs, Vat, out Error);

                case "udf.sparable":
                    return SetBool(Fs, Value, out var Sparable, out Error) && SetUdfSparable(Fs, Sparable, out Error);

                case "erofs.fragments":
                    return SetBool(Fs, Value, out var Frag, out Error) && SetEroFsFragments(Fs, Frag, out Error);

                case "erofs.tail-fragments":
                    return SetBool(Fs, Value, out var Tail, out Error) && SetEroFsTailFragments(Fs, Tail, out Error);

                case "erofs.shared-xattrs":
                    return SetBool(Fs, Value, out var Shared, out Error) && SetEroFsSharedXattrs(Fs, Shared, out Error);

                case "erofs.compression-level":
                    return SetEroFsLevel(Fs, Value, out Error);

                case "erofs.xattr-prefix":
                    return AddEroFsPrefix(Fs, Value, out Error);

                case "iso9660.boot-image":
                    Fs.IsoBootImage = Value;
                    return true;

                case "iso9660.boot-load-size":
                    return SetBootInt(Value, 0, 0xFFFF, out var BootSectors, out Error) &&
                        SetIsoBootSectors(Fs, BootSectors, out Error);

                case "iso9660.boot-load-segment":
                    return SetBootInt(Value, 0, 0xFFFF, out var BootSegment, out Error) &&
                        SetIsoBootSegment(Fs, (ushort)BootSegment, out Error);

                case "iso9660.boot-media-type":
                    return SetBootInt(Value, 0, 3, out var BootMedia, out Error) &&
                        SetIsoBootMedia(Fs, (byte)BootMedia, out Error);

                case "iso9660.boot-system-type":
                    return SetBootInt(Value, 0, 0xFF, out var BootSystem, out Error) &&
                        SetIsoBootSystem(Fs, (byte)BootSystem, out Error);

                case "iso9660.boot-identifier":
                    Fs.IsoBootIdentifier = Value;
                    return true;
                case "cramfs.endian":
                    return SetCramFsEndian(Fs, Value, out Error);
            }

            // Unprefixed aliases for a filesystem root (or a node with a known format).
            switch (Short)
            {
                case "compression":
                    switch (Fmt)
                    {
                        case "squashfs": return SetSquashFs(Fs, Value, out Error);
                        case "ubifs": return SetUbifs(Fs, Value, out Error);
                        case "f2fs": return SetF2fs(Fs, Value, out Error);
                        case "jffs2": return SetJffs2(Fs, Value, out Error);
                    }
                    break;

                case "type":
                    if (Fmt == "fat")
                        return SetFatType(Fs, Value, out Error);
                    break;

                case "vat":
                    if (Fmt == "udf")
                        return SetBool(Fs, Value, out var V, out Error) && SetUdfVat(Fs, V, out Error);
                    break;

                case "sparable":
                    if (Fmt == "udf")
                        return SetBool(Fs, Value, out var S, out Error) && SetUdfSparable(Fs, S, out Error);
                    break;

                case "container":
                    if (Fmt == "ubifs")
                        return Plugin.TryApplyOption(Fs, "container", Value, out Error);
                    break;

                case "fragments":
                    if (Fmt == "erofs")
                        return SetBool(Fs, Value, out var F, out Error) && SetEroFsFragments(Fs, F, out Error);
                    break;

                case "tail-fragments":
                    if (Fmt == "erofs")
                        return SetBool(Fs, Value, out var T, out Error) && SetEroFsTailFragments(Fs, T, out Error);
                    break;

                case "shared-xattrs":
                    if (Fmt == "erofs")
                        return SetBool(Fs, Value, out var X, out Error) && SetEroFsSharedXattrs(Fs, X, out Error);
                    break;

                case "compression-level":
                    if (Fmt == "erofs")
                        return SetEroFsLevel(Fs, Value, out Error);
                    break;

                case "xattr-prefix":
                    if (Fmt == "erofs")
                        return AddEroFsPrefix(Fs, Value, out Error);
                    break;

                case "boot-image":
                    if (Fmt == "iso9660") { Fs.IsoBootImage = Value; return true; }
                    break;

                case "boot-load-size":
                    if (Fmt == "iso9660")
                        return SetBootInt(Value, 0, 0xFFFF, out var BS, out Error) &&
                            SetIsoBootSectors(Fs, BS, out Error);
                    break;

                case "boot-load-segment":
                    if (Fmt == "iso9660")
                        return SetBootInt(Value, 0, 0xFFFF, out var BG, out Error) &&
                            SetIsoBootSegment(Fs, (ushort)BG, out Error);
                    break;

                case "boot-media-type":
                    if (Fmt == "iso9660")
                        return SetBootInt(Value, 0, 3, out var BM, out Error) &&
                            SetIsoBootMedia(Fs, (byte)BM, out Error);
                    break;

                case "boot-system-type":
                    if (Fmt == "iso9660")
                        return SetBootInt(Value, 0, 0xFF, out var BY, out Error) &&
                            SetIsoBootSystem(Fs, (byte)BY, out Error);
                    break;

                case "boot-identifier":
                    if (Fmt == "iso9660") { Fs.IsoBootIdentifier = Value; return true; }
                    break;
                case "endian":
                    if (Fmt == "cramfs")
                        return SetCramFsEndian(Fs, Value, out Error);
                    break;

                case "pagesize":
                case "page-size":
                    if (Fmt == "jffs2")
                        return SetJffs2Geometry(Fs, Value, true, out Error);
                    break;

                case "eraseblock":
                case "erase-block":
                    if (Fmt == "jffs2")
                        return SetJffs2Geometry(Fs, Value, false, out Error);
                    break;
            }

            return Plugin.TryApplyOption(Fs, Key, Value, out Error);
        }

        private static bool SetFsSize(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            var Size = PlanDocument.ParseSize(Value, out var Auto);
            if (Auto || !Size.HasValue || (Size.Value <= 0))
            {
                Error = $"Invalid filesystem size '{Value}'";
                return false;
            }

            Fs.FsSize = Size.Value;
            return true;
        }

        private static bool SetSquashFs(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            switch ((Value ?? "").ToLowerInvariant())
            {
                case "gzip": Fs.SquashFsCompression = SqCompressionType.Gzip; return true;
                case "lzma": Fs.SquashFsCompression = SqCompressionType.Lzma; return true;
                case "lz4": Fs.SquashFsCompression = SqCompressionType.Lz4; return true;
                case "zstd": Fs.SquashFsCompression = SqCompressionType.Zstd; return true;
            }

            Error = $"Unknown squashfs compression '{Value}' (gzip, lzma, lz4, zstd)";
            return false;
        }

        private static bool SetUbifs(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            switch ((Value ?? "").ToLowerInvariant())
            {
                case "none": Fs.UbiFsCompression = UbifsCompression.None; return true;
                case "lzo": Fs.UbiFsCompression = UbifsCompression.Lzo; return true;
                case "zlib": Fs.UbiFsCompression = UbifsCompression.Zlib; return true;
                case "zstd": Fs.UbiFsCompression = UbifsCompression.Zstd; return true;
            }

            Error = $"Unknown ubifs compression '{Value}' (none, lzo, zlib, zstd)";
            return false;
        }

        private static bool SetF2fs(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            switch ((Value ?? "").ToLowerInvariant())
            {
                case "none": Fs.F2fsCompression = F2fsCompression.None; return true;
                case "lzo": Fs.F2fsCompression = F2fsCompression.Lzo; return true;
                case "lz4": Fs.F2fsCompression = F2fsCompression.Lz4; return true;
                case "zstd": Fs.F2fsCompression = F2fsCompression.Zstd; return true;
                case "lzorle": Fs.F2fsCompression = F2fsCompression.LzoRle; return true;
            }

            Error = $"Unknown f2fs compression '{Value}' (none, lzo, lz4, zstd, lzorle)";
            return false;
        }

        private static bool SetF2fsCluster(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            var Size = PlanDocument.ParseSize(Value, out var Auto);
            if (Auto || !Size.HasValue || (Size.Value < 16 * 1024) || (Size.Value > 1024 * 1024) ||
                ((Size.Value & (Size.Value - 1)) != 0))
            {
                Error = "f2fs compression cluster must be a power of two between 16K and 1M";
                return false;
            }

            int Blocks = (int)(Size.Value / 4096);
            int Log = 0;
            while ((1 << Log) < Blocks)
                Log++;

            Fs.F2fsCompressionClusterLog = Log;
            return true;
        }

        private static bool SetJffs2(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            switch ((Value ?? "").ToLowerInvariant())
            {
                case "none": Fs.Jffs2Compression = Jffs2Compression.None; return true;
                case "zlib": Fs.Jffs2Compression = Jffs2Compression.Zlib; return true;
                case "rtime": Fs.Jffs2Compression = Jffs2Compression.Rtime; return true;
                case "lzo": Fs.Jffs2Compression = Jffs2Compression.Lzo; return true;
                case "priority": Fs.Jffs2Compression = Jffs2Compression.Priority; return true;
            }

            Error = $"Unknown jffs2 compression '{Value}' (none, zlib, rtime, lzo, priority)";
            return false;
        }

        private static bool SetJffs2Geometry(LinuxFilesystem Fs, string Value, bool Page, out string Error)
        {
            Error = null;
            var Size = PlanDocument.ParseSize(Value, out var Auto);
            if (Auto || !Size.HasValue || (Size.Value <= 0) || (Size.Value > Int32.MaxValue))
            {
                Error = $"Invalid jffs2 {(Page ? "page" : "eraseblock")} size '{Value}'";
                return false;
            }

            if (Page) Fs.Jffs2PageSize = (int)Size.Value;
            else Fs.Jffs2EraseBlockSize = (int)Size.Value;
            return true;
        }

        private static bool SetFatType(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            switch ((Value ?? "").ToLowerInvariant())
            {
                case "auto": Fs.FatType = 0; return true;
                case "fat12": Fs.FatType = 12; return true;
                case "fat16": Fs.FatType = 16; return true;
                case "fat32": Fs.FatType = 32; return true;
            }

            Error = $"Unknown fat type '{Value}' (auto, fat12, fat16, fat32)";
            return false;
        }

        private static bool SetBootInt(string Value, int Min, int Max, out int Result, out string Error)
        {
            Error = null;
            var Size = PlanDocument.ParseSize(Value, out var Auto);
            if (Auto || !Size.HasValue || (Size.Value < Min) || (Size.Value > Max))
            {
                Error = $"Invalid value '{Value}' (expected {Min}..{Max})";
                Result = 0;
                return false;
            }

            Result = (int)Size.Value;
            return true;
        }

        private static bool SetIsoBootSectors(LinuxFilesystem Fs, int Value, out string Error)
        {
            Error = null;
            Fs.IsoBootLoadSectors = Value;
            return true;
        }

        private static bool SetIsoBootSegment(LinuxFilesystem Fs, ushort Value, out string Error)
        {
            Error = null;
            Fs.IsoBootLoadSegment = Value;
            return true;
        }

        private static bool SetIsoBootMedia(LinuxFilesystem Fs, byte Value, out string Error)
        {
            Error = null;
            Fs.IsoBootMediaType = Value;
            return true;
        }

        private static bool SetIsoBootSystem(LinuxFilesystem Fs, byte Value, out string Error)
        {
            Error = null;
            Fs.IsoBootSystemType = Value;
            return true;
        }
        private static bool SetBool(LinuxFilesystem Fs, string Value, out bool Result, out string Error)
        {
            Error = null;
            switch ((Value ?? "").ToLowerInvariant())
            {
                case "on":
                case "true":
                case "yes":
                case "1":
                    Result = true;
                    return true;
                case "off":
                case "false":
                case "no":
                case "0":
                    Result = false;
                    return true;
            }

            Result = false;
            Error = $"Invalid boolean '{Value}'";
            return false;
        }

        private static bool SetUdfVat(LinuxFilesystem Fs, bool Value, out string Error)
        {
            Error = null;
            Fs.UdfVat = Value;
            if (Value) Fs.UdfSparable = false;
            return true;
        }

        private static bool SetUdfSparable(LinuxFilesystem Fs, bool Value, out string Error)
        {
            Error = null;
            Fs.UdfSparable = Value;
            if (Value) Fs.UdfVat = false;
            return true;
        }

        private static bool SetEroFsFragments(LinuxFilesystem Fs, bool Value, out string Error)
        {
            Error = null;
            Fs.EroFsFragments = Value;
            if (Value) Fs.EroFsTailFragments = false;
            return true;
        }

        private static bool SetEroFsTailFragments(LinuxFilesystem Fs, bool Value, out string Error)
        {
            Error = null;
            Fs.EroFsTailFragments = Value;
            if (Value) Fs.EroFsFragments = false;
            return true;
        }

        private static bool SetEroFsSharedXattrs(LinuxFilesystem Fs, bool Value, out string Error)
        {
            Error = null;
            Fs.EroFsSharedXattrs = Value;
            return true;
        }

        private static bool SetEroFsLevel(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            if (!Int32.TryParse(Value, out var Level) || !((Level == 0) || ((Level >= 3) && (Level <= 12))))
            {
                Error = "EROFS LZ4 level must be 0 or 3..12";
                return false;
            }

            Fs.EroFsCompressionLevel = Level;
            return true;
        }

        private static bool AddEroFsPrefix(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            var Prefix = (Value ?? "").Trim();
            if ((Prefix.Length == 0) ||
                (!Prefix.StartsWith("user.") && !Prefix.StartsWith("trusted.") &&
                 !Prefix.StartsWith("lustre.") && !Prefix.StartsWith("security.") &&
                 !Prefix.StartsWith("system.posix_acl_access") && !Prefix.StartsWith("system.posix_acl_default")))
            {
                Error = $"Invalid EROFS xattr prefix '{Value}'";
                return false;
            }

            if (!Fs.EroFsXattrPrefixes.Contains(Prefix))
                Fs.EroFsXattrPrefixes.Add(Prefix);
            return true;
        }

        private static bool SetCramFsEndian(LinuxFilesystem Fs, string Value, out string Error)
        {
            Error = null;
            switch ((Value ?? "").ToLowerInvariant())
            {
                case "big":
                case "be":
                    Fs.CramFsBigEndian = true;
                    return true;
                case "little":
                case "le":
                    Fs.CramFsBigEndian = false;
                    return true;
            }

            Error = $"Unknown cramfs endian '{Value}' (big, little)";
            return false;
        }
    }
}
