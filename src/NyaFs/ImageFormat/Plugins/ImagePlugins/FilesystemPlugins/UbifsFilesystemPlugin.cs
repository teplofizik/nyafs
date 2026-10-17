using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class UbifsFilesystemPlugin : Base.FilesystemPlugin
    {
        const uint UBI_EC_MAGIC = 0x55424923;      // "UBI#"
        const uint UBIFS_NODE_MAGIC = 0x06101831;
        const byte UBIFS_SB_NODE = 6;

        public UbifsFilesystemPlugin() : base("fs.ubifs", "ubifs", true, true, true)
        {

        }

        /// <summary>
        /// Is image a UBI container (holds a UBIFS volume)
        /// </summary>
        public override bool DetectFilesystem(ImageData Image) =>
            (Image.Length >= 4) && (Image.ReadUInt32BE(0) == UBI_EC_MAGIC);

        /// <summary>
        /// Get reader for the UBIFS volume inside the UBI image
        /// </summary>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data)
        {
            Filesystem.UbiFs.UbiFsReader Ubi;

            try
            {
                Ubi = new Filesystem.UbiFs.UbiFsReader(Data);
            }
            catch
            {
                return null;
            }

            foreach (var Vol in Ubi.Volumes)
            {
                if ((Vol.Name == null) || (Vol.LebToPeb.Count == 0))
                    continue;

                var Leb0 = Ubi.ReadLeb(Vol.Id, 0);
                if ((Leb0 != null) && (BitConverter.ToUInt32(Leb0, 0) == UBIFS_NODE_MAGIC) && (Leb0[0x14] == UBIFS_SB_NODE))
                    return new Elements.Fs.Reader.BaseFsReader(Types.FsType.UbiFs, new Filesystem.UbiFs.Ubifs.UbifsReader(Ubi, Vol));
            }

            return null;
        }

        /// <summary>
        /// Get writer: builds a UBIFS volume and wraps it into a UBI container.
        /// Default NAND geometry matches the common 128KiB PEB / 2KiB page layout
        /// (leb_size = 126976, min_io = 2048); the LEB count is derived from the content.
        /// </summary>
        public override Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs)
        {
            const int PebSize = 0x20000;
            const int PageSize = 0x800;

            var Ubifs = new Filesystem.UbiFs.Ubifs.UbifsBuilder(PebSize - 2 * PageSize, 10, PageSize);
            var Ubi = new Filesystem.UbiFs.UbiFsBuilder(Ubifs, PebSize, PageSize) { VolumeName = "rootfs" };
            return new Elements.Fs.Writer.BaseFsWriter(Ubi);
        }
    }
}
