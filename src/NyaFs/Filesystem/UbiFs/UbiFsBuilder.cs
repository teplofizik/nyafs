using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.UbiFs.Types;

namespace NyaFs.Filesystem.UbiFs
{
    /// <summary>
    /// Wraps a <see cref="Ubifs.UbifsBuilder"/> (raw UBIFS volume) into a UBI container:
    /// builds the layout volume (volume table) and the volume LEBs with the given geometry.
    /// </summary>
    public class UbiFsBuilder : IFilesystemBuilder, IFilesystemStreamBuilder, IXattrBuilder
    {
        public readonly Ubifs.UbifsBuilder Ubifs;

        public int PebSize;
        public int PageSize;
        public uint VolumeId = 0;
        public string VolumeName = "rootfs";
        public bool AutoResize = true;

        /// <summary>Compression for regular file data nodes (delegated to the UBIFS builder).</summary>
        public Ubifs.UbifsCompression Compression
        {
            get { return Ubifs.Compression; }
            set { Ubifs.Compression = value; }
        }

        public UbiFsBuilder(Ubifs.UbifsBuilder Ubifs, int PebSize = 0x20000, int PageSize = 0x800)
        {
            this.Ubifs = Ubifs;
            this.PebSize = PebSize;
            this.PageSize = PageSize;
        }

        public void Directory(string Path, uint User, uint Group, uint Mode) => Ubifs.Directory(Path, User, Group, Mode);
        public void File(string Path, byte[] Content, uint User, uint Group, uint Mode) => Ubifs.File(Path, Content, User, Group, Mode);
        public void SymLink(string Path, string Target, uint User, uint Group, uint Mode) => Ubifs.SymLink(Path, Target, User, Group, Mode);
        public void Char(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) => Ubifs.Char(Path, Major, Minor, User, Group, Mode);
        public void Block(string Path, uint Major, uint Minor, uint User, uint Group, uint Mode) => Ubifs.Block(Path, Major, Minor, User, Group, Mode);
        public void Fifo(string Path, uint User, uint Group, uint Mode) => Ubifs.Fifo(Path, User, Group, Mode);
        public void Socket(string Path, uint User, uint Group, uint Mode) => Ubifs.Socket(Path, User, Group, Mode);
        public void Xattr(string Path, string Name, byte[] Value) => Ubifs.Xattr(Path, Name, Value);

        public byte[] GetFilesystemImage()
        {
            using (var Stream = new MemoryStream())
            {
                WriteFilesystemImage(Stream);
                return Stream.ToArray();
            }
        }

        public void WriteFilesystemImage(Stream Output)
        {
            if (Output == null) throw new ArgumentNullException(nameof(Output));

            int LebSize = PebSize - 2 * PageSize;
            if (LebSize <= 0)
                throw new InvalidOperationException("UBI: PEB must be larger than two pages");

            if (!Output.CanSeek)
            {
                // The UBI layout (tables/headers before the payload) needs addressing;
                // build the raw volume in memory, then emit PEBs sequentially.
                WriteUbiFromVolume(Output, Ubifs.GetFilesystemImage(), LebSize);
                return;
            }

            long Start = Output.Position;

            Ubifs.WriteToStore(Len =>
            {
                int Lebs = (int)(Len / LebSize);
                var Builder = new UbiBuilder(PebSize, PageSize);
                Builder.AddReservedVolume(VolumeId, VolumeName, Lebs, VolumeType.UBI_VID_DYNAMIC, AutoResize);

                // Layout PEBs + EC/VID headers for all volume PEBs, pre-filled with 0xFF.
                Builder.WriteHeadersTo(Output);

                // UBIFS now fills the volume data areas directly.
                return new PebStore(Output, PebSize, 2 * PageSize, LebSize,
                    (int)UbiBuilder.UBI_LAYOUT_VOLUME_EBS, (long)Lebs * LebSize, Start);
            });

            Output.Flush();
        }

        private void WriteUbiFromVolume(Stream Output, byte[] Volume, int LebSize)
        {
            if (Volume == null)
                throw new InvalidOperationException("UBI: no UBIFS volume");

            var Builder = new UbiBuilder(PebSize, PageSize);
            var Vol = Builder.AddVolume(VolumeId, VolumeName, VolumeType.UBI_VID_DYNAMIC, AutoResize);

            int Lebs = Volume.Length / LebSize;
            for (int i = 0; i < Lebs; i++)
            {
                var L = new byte[LebSize];
                Array.Copy(Volume, (long)i * LebSize, L, 0, LebSize);
                Vol.Lebs.Add(L);
            }

            Builder.WriteTo(Output);
        }
    }
}
