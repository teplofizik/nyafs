using NyaFs.ImageFormat.Types;
using System;
using System.Collections.Generic;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    /// <summary>
    /// u-boot legacy image (uImage): a single-payload or multi-file container.
    /// Supports kernel/ramdisk/flat_dt/multi plus the remaining legacy header types
    /// (standalone, firmware, script, filesystem, kernel_noload, vendor boot images)
    /// which are exposed as opaque payloads (preserved and extractable).
    /// </summary>
    class LegacyImagePlugin : Base.CompositeImagePlugin
    {
        public LegacyImagePlugin() : base("image.legacy", "legacy") { }

        public override bool SupportWrite => true;

        public override bool PreserveUnknownChildren => true;

        public override bool DetectImage(ImageData Image)
        {
            if ((Image.Length < 0x40) || (Image.ReadUInt32BE(0) != 0x27051956u))
                return false;

            var Type = (ImageType)Image.ReadByte(0x1E);
            if ((Type == ImageType.IH_TYPE_INVALID) || (Type >= ImageType.IH_TYPE_COUNT))
                return false;

            // The declared payload must fit into the file (some dumps are truncated).
            var PayloadLength = Image.ReadUInt32BE(0x0C);
            return PayloadLength <= (Image.Length - 0x40);
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            var Img = new LegacyImage(Image.ReadArray(0, Image.Length));
            if (Img.Type == ImageType.IH_TYPE_MULTI)
                return GetMultiImages(Img);

            var Payload = Img.ImageData;
            if (Payload == null)
                return new ContainedImage[0];

            string Name;
            ImageKind Kind;
            string Format = null;
            switch (Img.Type)
            {
                case ImageType.IH_TYPE_KERNEL:
                case ImageType.IH_TYPE_KERNEL_NOLOAD:
                    Name = "kernel"; Kind = ImageKind.Kernel; Format = "legacy"; break;
                case ImageType.IH_TYPE_RAMDISK:
                case ImageType.IH_TYPE_FILESYSTEM:
                    Name = "ramdisk"; Kind = ImageKind.Filesystem; Format = "legacy"; break;
                case ImageType.IH_TYPE_FLATDT:
                    Name = "devtree"; Kind = ImageKind.Devtree; Format = "dtb"; break;
                default:
                    Name = "payload"; Kind = ImageKind.Unknown; break;
            }

            return new ContainedImage[]
            {
                new ContainedImage(Name, Kind, Format, -1, Payload.Length, Payload, Img.Compression, Img.Info,
                    Img.Name)
            };
        }

        private static ContainedImage[] GetMultiImages(LegacyImage Image)
        {
            var Data = Image.ImageData;
            var Sizes = new List<int>();
            var Position = 0;
            while (Position + 4 <= Data.Length)
            {
                var Size = (int)ReadU32BE(Data, Position);
                Position += 4;
                if (Size == 0)
                    break;
                if (Size < 0)
                    return new ContainedImage[0];
                Sizes.Add(Size);
            }
            if ((Position > Data.Length) || (Sizes.Count == 0))
                return new ContainedImage[0];

            var Result = new List<ContainedImage>();
            var DataOffset = Position;
            for (var I = 0; I < Sizes.Count; I++)
            {
                if ((DataOffset < 0) || (DataOffset + Sizes[I] > Data.Length))
                    break;
                var Content = new byte[Sizes[I]];
                Buffer.BlockCopy(Data, DataOffset, Content, 0, Content.Length);
                if (I == 0)
                {
                    var Info = new ImageInfo
                    {
                        Architecture = Image.CPUArchitecture,
                        OperatingSystem = Image.OperatingSystem,
                        Type = ImageType.IH_TYPE_KERNEL
                    };
                    Result.Add(new ContainedImage("image0", ImageKind.Kernel, "raw", -1, Content.Length, Content,
                        CompressionType.IH_COMP_NONE, Info, null, null, true));
                }
                else
                {
                    Result.Add(new ContainedImage($"image{I}", ImageKind.Unknown, null, -1, Content.Length, Content));
                }
                DataOffset += Align4(Sizes[I]);
            }
            return Result.ToArray();
        }

        private static uint ReadU32BE(byte[] Data, int Offset) =>
            (uint)((Data[Offset] << 24) | (Data[Offset + 1] << 16) | (Data[Offset + 2] << 8) | Data[Offset + 3]);

        private static int Align4(int Value) => (Value + 3) & ~3;

        public override Elements.Kernel.Reader.Reader GetKernelReader(byte[] Data)
        {
            var Img = new LegacyImage(Data);
            return IsKernel(Img.Type) ? new Elements.Kernel.Reader.LegacyReader(Data) : null;
        }

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data)
        {
            var Img = new LegacyImage(Data);
            return IsFilesystem(Img.Type) ? new Elements.Fs.Reader.LegacyReader(Data) : null;
        }

        public override Elements.Dtb.Reader.Reader GetDtbReader(byte[] Data)
        {
            var Img = new LegacyImage(Data);
            return (Img.Type == ImageType.IH_TYPE_FLATDT) ? new Elements.Dtb.Reader.LegacyReader(Data) : null;
        }

        private static bool IsKernel(ImageType Type) =>
            (Type == ImageType.IH_TYPE_KERNEL) || (Type == ImageType.IH_TYPE_KERNEL_NOLOAD);

        private static bool IsFilesystem(ImageType Type) =>
            (Type == ImageType.IH_TYPE_RAMDISK) || (Type == ImageType.IH_TYPE_FILESYSTEM);

        public override byte[] Build(BaseImageBlob Blob, out string Error)
        {
            Error = null;
            try
            {
                var Root = Blob.BuildPlan?.Root;
                if (Root?.Raw == null)
                {
                    Error = "Legacy image writer needs an existing source image";
                    return null;
                }

                if (!Root.Dirty)
                    return Root.Raw;

                var Source = new LegacyImage(Root.Raw);
                if (!Source.CorrectHeader)
                {
                    Error = "Invalid legacy header in the source image";
                    return null;
                }

                if (Source.Type == ImageType.IH_TYPE_MULTI)
                {
                    var Payloads = new List<byte[]>();
                    foreach (var Child in Root.Children)
                    {
                        var Payload = GetChildData(Child, CompressionType.IH_COMP_NONE);
                        if (Payload != null)
                            Payloads.Add(Payload);
                    }

                    if (Payloads.Count == 0)
                    {
                        Error = "Legacy multi image has no payloads";
                        return null;
                    }

                    return Composite.LegacyMultiImageWriter.BuildMulti(Payloads, Source.Info, Source.Name);
                }

                var Leaf = (Root.Children.Count > 0) ? Root.Children[0] : null;
                if (Leaf == null)
                    return Root.Raw;

                var Data = GetChildData(Leaf, Source.Compression, out var Compression);
                if (Data == null)
                {
                    Error = "Cannot serialize the legacy payload";
                    return null;
                }

                var Info = Source.Info.Clone();
                Info.Compression = Compression;
                return new LegacyImage(Info, Compression, Data).getPacket();
            }
            catch (Exception E)
            {
                Error = E.Message;
                return null;
            }
        }

        private static byte[] GetChildData(ImageBuildNode Node, CompressionType SourceCompression) =>
            GetChildData(Node, SourceCompression, out _);

        private static byte[] GetChildData(ImageBuildNode Node, CompressionType SourceCompression,
            out CompressionType Compression)
        {
            Compression = (Node.Compression != CompressionType.IH_COMP_NONE) ? Node.Compression : SourceCompression;

            if (!Node.Dirty && (Node.Raw != null))
                return Node.Raw;

            if (Node.Kind != ImageKind.Unknown)
            {
                var Writer = new Composite.ImageBuildPlanWriter(new ImageBuildPlan(Node));
                var Serialized = Writer.Build(out _);
                if (Serialized != null)
                    return Serialized;
            }

            // Unknown payload edited via import/use: the raw bytes are already in their final form.
            Compression = Node.Compression;
            return Node.Raw;
        }
    }
}
