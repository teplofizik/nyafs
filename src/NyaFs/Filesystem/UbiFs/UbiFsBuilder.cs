using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.UbiFs.Types;

namespace NyaFs.Filesystem.UbiFs
{
    /// <summary>
    /// Wraps a <see cref="Ubifs.UbifsBuilder"/> (raw UBIFS volume) into a UBI container:
    /// builds the layout volume (volume table) and the volume LEBs with the given geometry.
    /// </summary>
    public class UbiFsBuilder : IFilesystemBuilder
    {
        public readonly Ubifs.UbifsBuilder Ubifs;

        public int PebSize;
        public int PageSize;
        public uint VolumeId = 0;
        public string VolumeName = "rootfs";
        public bool AutoResize = true;

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

        public byte[] GetFilesystemImage()
        {
            var Volume = Ubifs.GetFilesystemImage();
            int LebSize = PebSize - 2 * PageSize;

            var Builder = new UbiBuilder(PebSize, PageSize);
            var Vol = Builder.AddVolume(VolumeId, VolumeName, VolumeType.UBI_VID_DYNAMIC, AutoResize);

            int Lebs = Volume.Length / LebSize;
            for (int i = 0; i < Lebs; i++)
            {
                var L = new byte[LebSize];
                Array.Copy(Volume, (long)i * LebSize, L, 0, LebSize);
                Vol.Lebs.Add(L);
            }

            return Builder.Build();
        }
    }
}
