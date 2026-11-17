using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.Filesystem.UbiFs.Types;

namespace NyaFs.Filesystem.UbiFs
{
    /// <summary>
    /// A UBI volume and the physical eraseblock holding each of its logical eraseblocks.
    /// </summary>
    public class UbiVolume
    {
        public uint Id;
        public string Name;
        public VolumeType Type;
        public uint ReservedPebs;

        /// <summary>lnum -> physical offset of the PEB</summary>
        public Dictionary<uint, long> LebToPeb = new Dictionary<uint, long>();

        public override string ToString() => $"VOL {Id:x8} '{Name}' type={Type} pebs={ReservedPebs} lebs={LebToPeb.Count}";
    }

    /// <summary>
    /// UBI (Unsorted Block Images) container reader: parses the erase-counter and volume
    /// identifier headers of every PEB, the volume table and the per-volume LEB mapping.
    /// </summary>
    public class UbiFsReader : RawPacket
    {
        public const uint UBI_EC_HDR_MAGIC = 0x55424923;   // "UBI#"
        public const uint UBI_VID_HDR_MAGIC = 0x55424921;  // "UBI!"
        public const uint UBI_LAYOUT_VOLUME_ID = 0x7FFFEFFF;

        public int PebSize { get; private set; }
        public int VidHdrOffset { get; private set; }
        public int DataOffset { get; private set; }
        public int LebSize => PebSize - DataOffset;

        public List<UbiVolume> Volumes { get; private set; } = new List<UbiVolume>();

        public UbiFsReader(byte[] Data) : base(Data)
        {
            Init();
        }

        public UbiFsReader(string Filename) : this(System.IO.File.ReadAllBytes(Filename)) { }

        private void Init()
        {
            if (!CheckMagic(0, UBI_EC_HDR_MAGIC))
                throw new InvalidOperationException("Invalid UBI image: no UBI# at offset 0");

            var Ec = new EraseCounterHeader(Raw, 0, 0x40);
            VidHdrOffset = Convert.ToInt32(Ec.VidHeaderOffset);
            DataOffset = Convert.ToInt32(Ec.DataSize);
            PebSize = DetectPebSize();

            if ((PebSize <= DataOffset) || (DataOffset <= 0x40))
                throw new InvalidOperationException($"Invalid UBI geometry: peb={PebSize} data_offset={DataOffset}");

            for (long Off = 0; Off + PebSize <= Raw.Length; Off += PebSize)
            {
                if (!CheckMagic(Off, UBI_EC_HDR_MAGIC))
                    continue;

                long VidOff = Off + VidHdrOffset;
                if (!CheckMagic(VidOff, UBI_VID_HDR_MAGIC))
                    continue;

                var Vid = new VolumeIdentifierHeader(Raw, VidOff, 0x40);

                if (Vid.VolumeId == UBI_LAYOUT_VOLUME_ID)
                    ReadVolumeTable(Off);
                else
                {
                    var Vol = GetOrCreateVolume(Vid.VolumeId);
                    if (!Vol.LebToPeb.ContainsKey(Vid.LNum))
                        Vol.LebToPeb[Vid.LNum] = Off;
                }
            }
        }

        private bool CheckMagic(long Offset, uint Magic) =>
            (Offset + 4 <= Raw.Length) && (Raw.ReadUInt32BE(Offset) == Magic);

        private int DetectPebSize()
        {
            // The next erase-counter header starts at the PEB boundary.
            for (int Size = 0x2000; Size <= 0x1000000; Size <<= 1)
            {
                if (Size >= Raw.Length)
                    break;

                if (CheckMagic(Size, UBI_EC_HDR_MAGIC))
                    return Size;
            }

            throw new InvalidOperationException("Invalid UBI image: cannot detect PEB size");
        }

        private UbiVolume GetOrCreateVolume(uint Id)
        {
            foreach (var V in Volumes)
                if (V.Id == Id)
                    return V;

            var Vol = new UbiVolume { Id = Id };
            Volumes.Add(Vol);
            return Vol;
        }

        /// <summary>
        /// Read the volume table from a layout-volume PEB (records start at data_offset).
        /// </summary>
        private void ReadVolumeTable(long PebOffset)
        {
            long Base = PebOffset + DataOffset;

            for (int i = 0; i < 128; i++)
            {
                long Off = Base + i * VolumeTableRecord.Size;
                if (Off + VolumeTableRecord.Size > Raw.Length)
                    break;

                var Rec = new VolumeTableRecord(Raw, Off);
                if (!Rec.IsCorrect || (Rec.NameLength == 0))
                    break;

                var Vol = GetOrCreateVolume(Convert.ToUInt32(i));
                Vol.Name = Rec.Name;
                Vol.Type = (VolumeType)Rec.VolumeType;
                Vol.ReservedPebs = Rec.ReservedPebs;
            }
        }

        /// <summary>
        /// Read a whole logical eraseblock (user data only, without the UBI headers).
        /// </summary>
        public byte[] ReadLeb(uint VolumeId, uint LNum)
        {
            var Vol = FindVolume(VolumeId);
            if ((Vol == null) || !Vol.LebToPeb.TryGetValue(LNum, out long Peb))
                return null;

            return ReadArray(Peb + DataOffset, LebSize);
        }

        public UbiVolume FindVolume(uint VolumeId)
        {
            foreach (var V in Volumes)
                if (V.Id == VolumeId)
                    return V;

            return null;
        }

        public UbiVolume FindVolume(string Name)
        {
            foreach (var V in Volumes)
                if (V.Name == Name)
                    return V;

            return null;
        }
    }
}
