using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Plugins.ImagePlugins.FilesystemPlugins
{
    class UbifsFilesystemPlugin : Base.FilesystemPlugin
    {
        public override NyaFs.ImageFormat.Types.FsType FsType => NyaFs.ImageFormat.Types.FsType.UbiFs;

        public override NyaFs.ImageFormat.Plugins.Base.FilesystemOption[] Options => new[]
        {
            new NyaFs.ImageFormat.Plugins.Base.FilesystemOption("container", NyaFs.ImageFormat.Plugins.Base.FilesystemOptionType.Enum, "ubi", "Output container", new[] { "ubi", "raw" })
        };

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
            ((Image.Length >= 4) && (Image.ReadUInt32BE(0) == UBI_EC_MAGIC)) ||
            ((Image.Length >= 0x28) && (Image.ReadUInt32(0) == UBIFS_NODE_MAGIC) && (Image.ReadByte(0x14) == UBIFS_SB_NODE));

        /// <summary>
        /// Get reader for the UBIFS volume inside the UBI image
        /// </summary>
        public override Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data)
        {
            if ((Data.Length >= 0x28) && (BitConverter.ToUInt32(Data, 0) == UBIFS_NODE_MAGIC) && (Data[0x14] == UBIFS_SB_NODE))
            {
                var LebSize = (int)BitConverter.ToUInt32(Data, 0x24);
                if ((LebSize <= 0x30 + 0x1000) || (LebSize > Data.Length))
                    return null;
                return new Elements.Fs.Reader.BaseFsReader(Types.FsType.UbiFs, new Filesystem.UbiFs.Ubifs.UbifsReader(Data, LebSize), 0, true);
            }

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

            var Ubifs = new Filesystem.UbiFs.Ubifs.UbifsBuilder(PebSize - 2 * PageSize, 10, PageSize) { Compression = Fs.UbiFsCompression };
            if (Fs.UbiFsRaw)
                return new Elements.Fs.Writer.BaseFsWriter(Ubifs);

            var Ubi = new Filesystem.UbiFs.UbiFsBuilder(Ubifs, PebSize, PageSize) { VolumeName = "rootfs" };
            return new Elements.Fs.Writer.BaseFsWriter(Ubi);
        }

        public override bool TryApplyOption(Elements.Fs.LinuxFilesystem Fs, string Name, string Value, out string Error)
        {
            Error = null;
            if (String.Equals(Name, "container", StringComparison.OrdinalIgnoreCase))
            {
                if (String.Equals(Value, "ubi", StringComparison.OrdinalIgnoreCase))
                {
                    Fs.UbiFsRaw = false;
                    return true;
                }
                if (String.Equals(Value, "raw", StringComparison.OrdinalIgnoreCase))
                {
                    Fs.UbiFsRaw = true;
                    return true;
                }
                Error = "ubifs.container expects ubi|raw";
                return false;
            }

            return base.TryApplyOption(Fs, Name, Value, out Error);
        }

        /// <summary>UBIFS stores dent names up to 255 bytes.</summary>
        public override bool CheckFilesystem(Elements.Fs.LinuxFilesystem Fs) => CheckNameLength(Fs, 255);
    }
}
