using NyaFs.ImageFormat.Types;
using System;
using System.Collections.Generic;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    /// <summary>
    /// u-boot legacy image (uImage): a single-payload or multi-file container.
    /// </summary>
    class LegacyImagePlugin : Base.CompositeImagePlugin
    {
        public LegacyImagePlugin() : base("image.legacy", "legacy") { }

        public override bool DetectImage(ImageData Image)
        {
            if ((Image.Length < 0x40) || (Image.ReadUInt32BE(0) != 0x27051956u))
                return false;

            var Type = (ImageType)Image.ReadByte(0x1E);
            return (Type == ImageType.IH_TYPE_KERNEL) ||
                   (Type == ImageType.IH_TYPE_RAMDISK) ||
                   (Type == ImageType.IH_TYPE_FLATDT) ||
                   (Type == ImageType.IH_TYPE_MULTI);
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
            switch (Img.Type)
            {
                case ImageType.IH_TYPE_KERNEL: Name = "kernel"; Kind = ImageKind.Kernel; break;
                case ImageType.IH_TYPE_RAMDISK: Name = "ramdisk"; Kind = ImageKind.Filesystem; break;
                case ImageType.IH_TYPE_FLATDT: Name = "devtree"; Kind = ImageKind.Devtree; break;
                default: Name = "payload"; Kind = ImageKind.Unknown; break;
            }

            return new ContainedImage[]
            {
                new ContainedImage(Name, Kind, "legacy", -1, Payload.Length, Payload, Img.Compression)
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
                Result.Add(new ContainedImage($"image{I}", ImageKind.Unknown, null, -1, Content.Length, Content));
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
            return (Img.Type == ImageType.IH_TYPE_KERNEL) ? new Elements.Kernel.Reader.LegacyReader(Data) : null;
        }

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data)
        {
            var Img = new LegacyImage(Data);
            return (Img.Type == ImageType.IH_TYPE_RAMDISK) ? new Elements.Fs.Reader.LegacyReader(Data) : null;
        }
    }
}