using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Composite;
using NyaFs.ImageFormat.Plugins.Base;
using NyaFs.ImageFormat.Types;
using NyaFs.ImageFormat.Types.Android;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    /// <summary>
    /// Android vendor_boot image (v3/v4). Vendor ramdisk fragments, DTB and bootconfig
    /// are exposed as opaque children so the normal pipeline detects cpio/CramFS/DTB
    /// inside them.
    /// </summary>
    class VendorBootPlugin : CompositeImagePlugin
    {
        public VendorBootPlugin() : base("image.vendorboot", "vendorboot") { }

        public override bool PreserveUnknownChildren => true;

        public override bool SupportWrite => true;

        public override bool DetectImage(ImageData Image)
        {
            if (Image.Length < VendorBootImage.HeaderV3Size)
                return false;

            return new VendorBootImage(Image.ReadArray(0, Image.Length)).Correct;
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            var Raw = Image.ReadArray(0, Image.Length);
            var Boot = new VendorBootImage(Raw);
            if (!Boot.Correct)
                return new ContainedImage[0];

            var Result = new List<ContainedImage>();
            if (Boot.HeaderVersion >= 4)
            {
                for (var I = 0; I < Boot.Entries.Count; I++)
                {
                    var Entry = Boot.Entries[I];
                    var Content = Boot.GetEntryData(Entry);
                    Result.Add(new ContainedImage($"vendor_ramdisk_{I}", ImageKind.Unknown, null,
                        Entry.FileOffset(Boot.VendorRamdiskOffset), Entry.Size, Content));
                }
            }
            else
            {
                var Content = Boot.VendorRamdisk;
                if (Content.Length > 0)
                    Result.Add(new ContainedImage("vendor_ramdisk", ImageKind.Unknown, null,
                        Boot.VendorRamdiskOffset, Boot.VendorRamdiskSize, Content));
            }

            var Dtb = Boot.Dtb;
            if (Dtb.Length > 0)
                Result.Add(new ContainedImage("dtb", ImageKind.Unknown, "dtb",
                    Boot.DtbOffset, Boot.DtbSize, Dtb));

            if (Boot.HeaderVersion >= 4)
            {
                var Bootconfig = Boot.Bootconfig;
                if (Bootconfig.Length > 0)
                    Result.Add(new ContainedImage("bootconfig", ImageKind.Unknown, null,
                        Boot.BootconfigOffset, Boot.BootconfigSize, Bootconfig));
            }

            return Result.ToArray();
        }

        public override IEnumerable<KeyValuePair<string, string>> ReadOptions(ImageData Image)
        {
            var Boot = new VendorBootImage(Image.ReadArray(0, Image.Length));
            if (!Boot.Correct)
                yield break;

            yield return new KeyValuePair<string, string>("header-version", Boot.HeaderVersion.ToString());
            yield return new KeyValuePair<string, string>("page-size", Boot.PageSize.ToString());
            if (!String.IsNullOrEmpty(Boot.Name))
                yield return new KeyValuePair<string, string>("name", Boot.Name);
            if (!String.IsNullOrEmpty(Boot.Cmdline))
                yield return new KeyValuePair<string, string>("cmdline", Boot.Cmdline);
            yield return new KeyValuePair<string, string>("vendor-ramdisk-size", Boot.VendorRamdiskSize.ToString());
            yield return new KeyValuePair<string, string>("dtb-size", Boot.DtbSize.ToString());
            if (Boot.HeaderVersion >= 4)
            {
                yield return new KeyValuePair<string, string>("ramdisk-count", Boot.Entries.Count.ToString());
                yield return new KeyValuePair<string, string>("bootconfig-size", Boot.BootconfigSize.ToString());
            }
        }

        public override string ReadDescription(ImageData Image)
        {
            var Boot = new VendorBootImage(Image.ReadArray(0, Image.Length));
            if (!Boot.Correct)
                return null;

            var Name = String.IsNullOrEmpty(Boot.Name) ? "" : " " + Boot.Name;
            return $"Android vendor_boot v{Boot.HeaderVersion}{Name}";
        }

        public override byte[] Build(BaseImageBlob Blob, out string Error)
        {
            Error = null;
            try
            {
                var Root = Blob.BuildPlan?.Root;
                if (Root?.Raw == null)
                {
                    Error = "Vendor boot writer needs an existing source image";
                    return null;
                }

                if (!Root.Dirty)
                    return Root.Raw;

                var Source = new VendorBootImage(Root.Raw);
                if (!Source.Correct)
                {
                    Error = "Invalid vendor_boot source image";
                    return null;
                }

                return (Source.HeaderVersion >= 4)
                    ? BuildV4(Source, Root, out Error)
                    : BuildV3(Source, Root, out Error);
            }
            catch (Exception E)
            {
                Error = E.Message;
                return null;
            }
        }

        private static byte[] BuildV3(VendorBootImage Source, ImageBuildNode Root, out string Error)
        {
            Error = null;
            var RamdiskChild = FindChild(Root, "vendor_ramdisk");
            var DtbChild = FindChild(Root, "dtb");

            var Ramdisk = ((RamdiskChild != null) && IsDirty(RamdiskChild))
                ? GetNodeData(RamdiskChild) : Source.VendorRamdisk;
            var Dtb = ((DtbChild != null) && IsDirty(DtbChild))
                ? GetNodeData(DtbChild) : Source.Dtb;
            if (Ramdisk == null)
                Ramdisk = new byte[0];
            if (Dtb == null)
                Dtb = new byte[0];

            var Header = CopyHeader(Source);
            WriteU32(Header, 0x18, (uint)Ramdisk.Length);
            WriteU32(Header, 0x834, (uint)Dtb.Length);

            var Total = Align((long)Source.PageSize + Ramdisk.Length, Source.PageSize);
            Total = Align(Total + Dtb.Length, Source.PageSize);
            var Result = new byte[Total];

            Buffer.BlockCopy(Header, 0, Result, 0, (int)Source.PageSize);
            Buffer.BlockCopy(Ramdisk, 0, Result, (int)Source.PageSize, Ramdisk.Length);
            Buffer.BlockCopy(Dtb, 0, Result, (int)Align((long)Source.PageSize + Ramdisk.Length, Source.PageSize), Dtb.Length);
            return Result;
        }

        private static byte[] BuildV4(VendorBootImage Source, ImageBuildNode Root, out string Error)
        {
            Error = null;
            var Fragments = new List<byte[]>();
            var TableEntries = new List<VendorBootImage.Entry>();

            for (var I = 0; I < Source.Entries.Count; I++)
            {
                var Entry = Source.Entries[I];
                var Child = FindChild(Root, $"vendor_ramdisk_{I}");
                var Data = ((Child != null) && IsDirty(Child)) ? GetNodeData(Child) : Source.GetEntryData(Entry);
                if (Data == null)
                    Data = new byte[0];

                var NewEntry = new VendorBootImage.Entry
                {
                    Size = (uint)Data.Length,
                    Offset = (uint)FragmentsSize(Fragments),
                    Type = Entry.Type,
                    Name = Entry.Name,
                    BoardId = Entry.BoardId
                };
                Fragments.Add(Data);
                TableEntries.Add(NewEntry);
            }

            var DtbChild = FindChild(Root, "dtb");
            var Dtb = ((DtbChild != null) && IsDirty(DtbChild)) ? GetNodeData(DtbChild) : Source.Dtb;
            if (Dtb == null)
                Dtb = new byte[0];

            var BootconfigChild = FindChild(Root, "bootconfig");
            var Bootconfig = ((BootconfigChild != null) && IsDirty(BootconfigChild))
                ? GetNodeData(BootconfigChild) : Source.Bootconfig;
            if (Bootconfig == null)
                Bootconfig = new byte[0];

            var RamdiskRegion = new byte[FragmentsSize(Fragments)];
            var Position = 0;
            foreach (var Fragment in Fragments)
            {
                Buffer.BlockCopy(Fragment, 0, RamdiskRegion, Position, Fragment.Length);
                Position += Fragment.Length;
            }

            var EntrySize = (Source.TableEntrySize >= VendorBootImage.TableEntryV4Size)
                ? (int)Source.TableEntrySize : VendorBootImage.TableEntryV4Size;
            var Table = new byte[EntrySize * TableEntries.Count];
            for (var I = 0; I < TableEntries.Count; I++)
            {
                var Entry = TableEntries[I];
                var Offset = I * EntrySize;
                WriteU32(Table, Offset + 0, Entry.Size);
                WriteU32(Table, Offset + 4, Entry.Offset);
                WriteU32(Table, Offset + 8, Entry.Type);
                WriteString(Table, Offset + 12, 32, Entry.Name);
                for (var B = 0; B < 16; B++)
                    WriteU32(Table, Offset + 44 + B * 4, (Entry.BoardId != null && B < Entry.BoardId.Length) ? Entry.BoardId[B] : 0);
            }

            var Header = CopyHeader(Source);
            WriteU32(Header, 0x18, (uint)RamdiskRegion.Length);
            WriteU32(Header, 0x834, (uint)Dtb.Length);
            WriteU32(Header, 0x840, (uint)Table.Length);
            WriteU32(Header, 0x844, (uint)TableEntries.Count);
            WriteU32(Header, 0x848, (uint)EntrySize);
            WriteU32(Header, 0x84C, (uint)Bootconfig.Length);

            var Page = Source.PageSize;
            var RamdiskOffset = (long)Page;
            var DtbOffset = Align(RamdiskOffset + RamdiskRegion.Length, Page);
            var TableOffset = Align(DtbOffset + Dtb.Length, Page);
            var BootconfigOffset = Align(TableOffset + Table.Length, Page);
            var Total = Align(BootconfigOffset + Bootconfig.Length, Page);
            var Result = new byte[Total];

            Buffer.BlockCopy(Header, 0, Result, 0, (int)Page);
            Buffer.BlockCopy(RamdiskRegion, 0, Result, (int)RamdiskOffset, RamdiskRegion.Length);
            Buffer.BlockCopy(Dtb, 0, Result, (int)DtbOffset, Dtb.Length);
            Buffer.BlockCopy(Table, 0, Result, (int)TableOffset, Table.Length);
            Buffer.BlockCopy(Bootconfig, 0, Result, (int)BootconfigOffset, Bootconfig.Length);
            return Result;
        }

        private static byte[] CopyHeader(VendorBootImage Source)
        {
            var Header = new byte[Source.PageSize];
            var Length = (int)Math.Min(Source.PageSize, Source.ReadArray(0, Source.PageSize).Length);
            Buffer.BlockCopy(Source.ReadArray(0, Length), 0, Header, 0, Length);
            return Header;
        }

        private static int FragmentsSize(List<byte[]> Fragments)
        {
            var Size = 0;
            foreach (var Fragment in Fragments)
                Size += Fragment.Length;
            return Size;
        }

        private static ImageBuildNode FindChild(ImageBuildNode Root, string Name)
        {
            foreach (var Child in Root.Children)
                if (String.Equals(Child.Name, Name, StringComparison.OrdinalIgnoreCase))
                    return Child;
            return null;
        }

        private static bool IsDirty(ImageBuildNode Node)
        {
            if (Node.Dirty)
                return true;
            foreach (var Child in Node.Children)
                if (IsDirty(Child))
                    return true;
            return false;
        }

        private static byte[] GetNodeData(ImageBuildNode Node)
        {
            if (!Node.Dirty && (Node.Raw != null))
                return Node.Raw;

            if (Node.Kind != ImageKind.Unknown)
            {
                var Writer = new ImageBuildPlanWriter(new ImageBuildPlan(Node));
                var Result = Writer.Build(out _);
                if (Result != null)
                    return Result;
            }

            return Node.Raw;
        }

        private static long Align(long Value, long Alignment) =>
            ((Value + Alignment - 1) / Alignment) * Alignment;

        private static void WriteU32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)Value;
            Data[Offset + 1] = (byte)(Value >> 8);
            Data[Offset + 2] = (byte)(Value >> 16);
            Data[Offset + 3] = (byte)(Value >> 24);
        }

        private static void WriteString(byte[] Data, int Offset, int Size, string Value)
        {
            var Bytes = Encoding.ASCII.GetBytes(Value ?? "");
            Buffer.BlockCopy(Bytes, 0, Data, Offset, Math.Min(Size, Bytes.Length));
        }
    }
}
