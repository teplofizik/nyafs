namespace NyaFs.ImageFormat.Plugins.ImagePlugins.DtbPlugins
{
    /// <summary>
    /// Device tree source (.dts) writer plugin (write-only; for exporting a loaded DTB to text).
    /// </summary>
    class DtsPlugin : Base.DevtreePlugin
    {
        public DtsPlugin() : base("dts", "dts", false, true, true) { }

        public override Elements.Dtb.Writer.Writer GetDevtreeWriter(Elements.Dtb.DeviceTree Dtb) =>
            new Elements.Dtb.Writer.DtsWriter();
    }
}
