namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    /// <summary>
    /// CvImage (CIMG) vendor container holding a gzip-compressed ramfs.
    /// </summary>
    class CImgFilesystemPlugin : Base.FilesystemPlugin
    {
        public CImgFilesystemPlugin() : base("fs.cimg", "cimg", true, false, true) { }

        public override bool DetectFilesystem(ImageData Image)
        {
            if ((Image.Length < 0x80) || (Image.ReadUInt32(0) != 0x474d4943u))
                return false;

            try
            {
                return new Types.CvImage(Image.ReadArray(0, Image.Length)).Correct;
            }
            catch
            {
                return false;
            }
        }

        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) =>
            new Elements.Fs.Reader.CImgReader(Data);
    }
}
