namespace NyaFs.ImageFormat.Plugins.ImagePlugins.DtbPlugins
{
    /// <summary>
    /// Plain flattened device tree (DTB, magic 0xD00DFEED).
    /// </summary>
    class DtbPlugin : Base.DevtreePlugin
    {
        public DtbPlugin() : base("dtb", "dtb", true, true, true) { }

        public override bool DetectDevtree(ImageData Image) =>
            (Image.Length >= 4) && (Image.ReadUInt32(0) == 0xedfe0dd0u);

        public override Elements.Dtb.Reader.Reader GetDevtreeReader(byte[] Data) =>
            new Elements.Dtb.Reader.DtbReader(Data);

        public override Elements.Dtb.Writer.Writer GetDevtreeWriter(Elements.Dtb.DeviceTree Dtb) =>
            new Elements.Dtb.Writer.FdtWriter();
    }
}
