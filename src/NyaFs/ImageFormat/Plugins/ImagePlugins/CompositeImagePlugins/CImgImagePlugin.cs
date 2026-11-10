using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Plugins.Base;
using NyaFs.ImageFormat.Types;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.CompositeImagePlugins
{
    /// <summary>
    /// CvImage (CIMG) vendor container. The container itself is not a filesystem:
    /// it carries a gzip-compressed payload whose kind is detected recursively.
    /// </summary>
    public class CImgImagePlugin : CompositeImagePlugin
    {
        public CImgImagePlugin() : base("image.cimg", "cimg") { }

        public override bool DetectImage(ImageData Image)
        {
            if ((Image.Length < 0x80) || (Image.ReadUInt32(0) != 0x474D4943u))
                return false;

            try
            {
                return new CvImage(Image.ReadArray(0, Image.Length)).Correct;
            }
            catch
            {
                return false;
            }
        }

        public override ContainedImage[] GetImages(ImageData Image)
        {
            try
            {
                var Cimg = new CvImage(Image.ReadArray(0, Image.Length));
                if (!Cimg.Correct || (Cimg.Content == null))
                    return new ContainedImage[0];

                return new ContainedImage[]
                {
                    new ContainedImage("payload", ImageKind.Unknown, "cimg", 0x80,
                        Cimg.ImageSize, Cimg.Content, CompressionType.IH_COMP_NONE)
                };
            }
            catch
            {
                return new ContainedImage[0];
            }
        }
    }
}
