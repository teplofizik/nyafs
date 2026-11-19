using System;
using System.Collections.Generic;
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
            using (var Stream = new System.IO.MemoryStream())
            {
                if (!BuildTo(Stream, Root, Plugins, out Error))
                    return null;

                return Stream.ToArray();
            }
        }

        private sealed class VolumePlan
        {
            public ImageBuildNode Child;
            public uint Id;
            public string Name;
            public VolumeType Type;
            public bool AutoResize;
            public int BasePeb;
            public int Lebs;
            public UbifsBuilder Ubifs;
            public byte[] Data;
        }

        public static bool BuildTo(System.IO.Stream Output, ImageBuildNode Root,
            PluginRegistry Plugins, out string Error)
        {
            Error = null;

            if ((Output == null) || !Output.CanWrite)
            {
                Error = "UBI output stream is not writable";
                return false;
            }

            var PebSize = (int)Option(Root, "peb", Option(Root, "block", 0x20000));
            var PageSize = (int)Option(Root, "page", 0x800);

            if ((PebSize <= 0) || (PageSize <= 0) || (PageSize & (PageSize - 1)) != 0 ||
                (PebSize <= 2 * PageSize))
            {
                Error = $"Invalid UBI geometry: peb={PebSize}, page={PageSize}";
                return false;
            }

            var LebSize = PebSize - 2 * PageSize;

            Root.Options.TryGetValue("plan.base", out var BaseDir);
            Root.Options.TryGetValue("plan.blobs", out var BlobDir);

            var Plans = new List<VolumePlan>();
            int BasePeb = (int)UbiBuilder.UBI_LAYOUT_VOLUME_EBS;
            uint NextId = 0;

            foreach (var Child in Root.Children)
            {
                var Name = Child.Name;
                if (String.IsNullOrEmpty(Name))
                {
                    Error = "UBI volume without a name";
                    return false;
                }

                var Type = ParseType(Get(Child, "volume-type", Get(Child, "type", null)), Child.Format);
                var Id = (uint)Option(Child, "volume-id", NextId);
                var AutoResize = ParseBool(Get(Child, "autoresize", null), Type == VolumeType.UBI_VID_DYNAMIC);

                var Plan = new VolumePlan
                {
                    Child = Child,
                    Name = Name,
                    Id = Id,
                    Type = Type,
                    AutoResize = AutoResize,
                    BasePeb = BasePeb
                };

                if ((Child.Format == "ubifs") && (Child.Content != null) && !String.IsNullOrEmpty(Child.Content.Dir))
                {
                    Plan.Ubifs = CreateUbifs(Child, LebSize, PageSize, BaseDir, Plugins, out Error);
                    if (Plan.Ubifs == null)
                        return false;

                    Plan.Lebs = Plan.Ubifs.PrepareLayout();
                }
                else
                {
                    if (!PlanNodeSerializer.TrySerialize(Child, Plugins, BaseDir, BlobDir,
                        out var Data, out var Skip, out Error))
                        return false;

                    if (Skip || (Data == null) || (Data.Length == 0))
                    {
                        Error = $"UBI volume '{Name}' has no content";
                        return false;
                    }

                    Plan.Data = Data;
                    Plan.Lebs = (Data.Length + LebSize - 1) / LebSize;
                }

                BasePeb += Plan.Lebs;
                Plans.Add(Plan);
                NextId = Math.Max(NextId, Id) + 1;
            }

            if (Plans.Count == 0)
            {
                Error = "UBI layout needs at least one volume";
                return false;
            }

            if (!Output.CanSeek)
                return WriteInMemory(Output, Plans, PebSize, PageSize, LebSize, Root, out Error);

            long Start = Output.Position;

            var Ubi = new UbiBuilder(PebSize, PageSize);
            foreach (var Plan in Plans)
            {
                if (Plan.Ubifs != null)
                {
                    Ubi.AddReservedVolume(Plan.Id, Plan.Name, Plan.Lebs, Plan.Type, Plan.AutoResize);
                }
                else
                {
                    Ubi.AddReservedStaticVolume(Plan.Id, Plan.Name, Plan.Lebs,
                        (uint)Plan.Data.Length, (uint)Plan.Lebs, Crc32.CalcCrc(Plan.Data), Plan.AutoResize);
                }
            }

            // Layout PEBs + EC/VID headers for every volume, pre-filled with 0xFF.
            Ubi.WriteHeadersTo(Output);

            foreach (var Plan in Plans)
            {
                if (Plan.Ubifs != null)
                {
                    var P = Plan;
                    P.Ubifs.WritePreparedToStore(Len =>
                        new PebStore(Output, PebSize, 2 * PageSize, LebSize, P.BasePeb, Len, Start));
                }
                else
                {
                    var Store = new PebStore(Output, PebSize, 2 * PageSize, LebSize,
                        Plan.BasePeb, (long)Plan.Lebs * LebSize, Start);
                    Store.Write(0, Plan.Data, 0, Plan.Data.Length);
                }
            }

            Output.Flush();

            var Target = Option(Root, "total", Option(Root, "size", 0));
            if (Target > 0)
            {
                if (Ubi.ImageLength > Target)
                {
                    Error = $"UBI image is 0x{Ubi.ImageLength:x} bytes, larger than the target 0x{Target:x}";
                    return false;
                }

                WriteZeros(Output, Target - Ubi.ImageLength);
            }

            return true;
        }

        private static bool WriteInMemory(System.IO.Stream Output, List<VolumePlan> Plans,
            int PebSize, int PageSize, int LebSize, ImageBuildNode Root, out string Error)
        {
            Error = null;

            var Ubi = new UbiBuilder(PebSize, PageSize);
            foreach (var Plan in Plans)
            {
                byte[] Data = Plan.Ubifs != null ? Plan.Ubifs.GetFilesystemImage() : Plan.Data;
                if ((Data == null) || (Data.Length == 0))
                {
                    Error = $"UBI volume '{Plan.Name}' has no content";
                    return false;
                }

                var Volume = Ubi.AddVolume(Plan.Id, Plan.Name, Plan.Type, Plan.AutoResize);
                for (var Offset = 0; Offset < Data.Length; Offset += LebSize)
                {
                    var Length = Math.Min(LebSize, Data.Length - Offset);
                    var Leb = new byte[Length];
                    Array.Copy(Data, Offset, Leb, 0, Length);
                    Volume.Lebs.Add(Leb);
                }
            }

            Ubi.WriteTo(Output);

            var Target = Option(Root, "total", Option(Root, "size", 0));
            if (Target > 0)
            {
                if (Ubi.ImageLength > Target)
                {
                    Error = $"UBI image is 0x{Ubi.ImageLength:x} bytes, larger than the target 0x{Target:x}";
                    return false;
                }

                WriteZeros(Output, Target - Ubi.ImageLength);
            }

            return true;
        }

        private static void WriteZeros(System.IO.Stream Output, long Count)
        {
            var Buffer = new byte[64 * 1024];
            for (var I = 0; I < Buffer.Length; I++)
                Buffer[I] = 0xFF;

            while (Count > 0)
            {
                var Chunk = (int)Math.Min(Count, Buffer.Length);
                Output.Write(Buffer, 0, Chunk);
                Count -= Chunk;
            }
        }
        private static UbifsBuilder CreateUbifs(ImageBuildNode Child, int LebSize, int PageSize,
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

            BaseFsWriter.Populate(Builder, Fs);
            return Builder;
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
