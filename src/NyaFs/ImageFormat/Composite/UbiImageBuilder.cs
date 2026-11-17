using System;
using NyaFs.Filesystem.UbiFs;
using NyaFs.Filesystem.UbiFs.Types;
using NyaFs.Filesystem.UbiFs.Ubifs;
using NyaFs.ImageFormat.Elements.Fs;
using NyaFs.ImageFormat.Elements.Fs.Writer;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Builds a multi-volume UBI container from layout children. Each child becomes
    /// one volume: `format: ubifs` + `content.dir` is packed as a raw UBIFS volume;
    /// other formats are static volumes padded to LEB boundaries.
    /// </summary>
    public static class UbiImageBuilder
    {
        public static byte[] Build(ImageBuildNode Root, PluginRegistry Plugins, out string Error)
        {
            Error = null;

            var PebSize = (int)Option(Root, "peb", Option(Root, "block", 0x20000));
            var PageSize = (int)Option(Root, "page", 0x800);

            if ((PebSize <= 0) || (PageSize <= 0) || (PageSize & (PageSize - 1)) != 0 ||
                (PebSize <= 2 * PageSize))
            {
                Error = $"Invalid UBI geometry: peb={PebSize}, page={PageSize}";
                return null;
            }

            var LebSize = PebSize - 2 * PageSize;
            var Ubi = new UbiBuilder(PebSize, PageSize);

            Root.Options.TryGetValue("plan.base", out var BaseDir);
            Root.Options.TryGetValue("plan.blobs", out var BlobDir);

            uint NextId = 0;
            foreach (var Child in Root.Children)
            {
                var Name = Child.Name;
                if (String.IsNullOrEmpty(Name))
                {
                    Error = "UBI volume without a name";
                    return null;
                }

                var Type = ParseType(Get(Child, "volume-type", Get(Child, "type", null)), Child.Format);
                var Id = (uint)Option(Child, "volume-id", NextId);
                var AutoResize = ParseBool(Get(Child, "autoresize", null), Type == VolumeType.UBI_VID_DYNAMIC);

                byte[] Data;
                if ((Child.Format == "ubifs") && (Child.Content != null) && !String.IsNullOrEmpty(Child.Content.Dir))
                {
                    Data = BuildUbifs(Child, LebSize, PageSize, BaseDir, Plugins, out Error);
                    if (Data == null)
                        return null;
                }
                else
                {
                    if (!PlanNodeSerializer.TrySerialize(Child, Plugins, BaseDir, BlobDir,
                        out Data, out var Skip, out Error))
                        return null;

                    if (Skip || (Data == null) || (Data.Length == 0))
                    {
                        Error = $"UBI volume '{Name}' has no content";
                        return null;
                    }
                }

                var Volume = Ubi.AddVolume(Id, Name, Type, AutoResize);
                for (var Offset = 0; Offset < Data.Length; Offset += LebSize)
                {
                    var Length = Math.Min(LebSize, Data.Length - Offset);
                    var Leb = new byte[Length];
                    Array.Copy(Data, Offset, Leb, 0, Length);
                    Volume.Lebs.Add(Leb);
                }

                NextId = Math.Max(NextId, Id) + 1;
            }

            if (Ubi.VolumeCount == 0)
            {
                Error = "UBI layout needs at least one volume";
                return null;
            }

            var Result = Ubi.Build();
            var Target = Option(Root, "total", Option(Root, "size", 0));

            if (Target > 0)
            {
                if (Target > Int32.MaxValue)
                {
                    Error = "UBI target size is too large";
                    return null;
                }

                if (Result.Length > Target)
                {
                    Error = $"UBI image is 0x{Result.Length:x} bytes, larger than the target 0x{Target:x}";
                    return null;
                }

                if (Result.Length < Target)
                {
                    var Padded = new byte[(int)Target];
                    Array.Copy(Result, Padded, Result.Length);
                    for (var I = Result.Length; I < Padded.Length; I++)
                        Padded[I] = 0xFF;
                    Result = Padded;
                }
            }

            return Result;
        }

        private static byte[] BuildUbifs(ImageBuildNode Child, int LebSize, int PageSize,
            string BaseDir, PluginRegistry Plugins, out string Error)
        {
            var DirPath = PlanContentResolver.ResolvePath(Child.Content.Dir, BaseDir);
            if (!System.IO.Directory.Exists(DirPath))
            {
                Error = $"Content directory '{DirPath}' for UBI volume '{Child.Name}' is not found";
                return null;
            }

            var Fs = PlanContentResolver.BuildFilesystemObject("ubifs", DirPath, Child.Content, out Error);
            if (Fs == null)
                return null;

            if (!FilesystemPlanOptions.Apply(Fs, "ubifs", Child.Options, Plugins, out Error))
                return null;

            Fs.UbiFsRaw = true;
            var Builder = new UbifsBuilder(LebSize, 10, PageSize)
            {
                Compression = Fs.UbiFsCompression
            };

            var Writer = new BaseFsWriter(Builder);
            Writer.WriteFs(Fs);
            return Writer.RawStream;
        }

        private static VolumeType ParseType(string Value, string Format)
        {
            if (!String.IsNullOrWhiteSpace(Value))
            {
                switch (Value.Trim().ToLowerInvariant())
                {
                    case "static": return VolumeType.UBI_VID_STATIC;
                    case "dynamic": return VolumeType.UBI_VID_DYNAMIC;
                }
            }

            return String.Equals(Format, "ubifs", StringComparison.OrdinalIgnoreCase)
                ? VolumeType.UBI_VID_DYNAMIC
                : VolumeType.UBI_VID_STATIC;
        }

        private static string Get(ImageBuildNode Node, string Key, string Default) =>
            Node.Options.TryGetValue(Key, out var Value) ? Value : Default;

        private static long Option(ImageBuildNode Node, string Key, long Default)
        {
            if (!Node.Options.TryGetValue(Key, out var Text) || String.IsNullOrWhiteSpace(Text))
                return Default;

            var Value = PlanDocument.ParseSize(Text, out var Auto);
            return (Auto || !Value.HasValue) ? Default : Value.Value;
        }

        private static bool ParseBool(string Value, bool Default)
        {
            if (String.IsNullOrWhiteSpace(Value))
                return Default;

            switch (Value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on": return true;
                case "0":
                case "false":
                case "no":
                case "off": return false;
                default: return Default;
            }
        }
    }
}
