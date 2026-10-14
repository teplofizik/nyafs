using System;
using NyaIO.Data;

namespace NyaFs.ImageFormat
{
    /// <summary>
    /// Read-only, non-copying view over a byte range.
    /// Detectors and loaders receive this so they can read magic/headers at offsets
    /// (including sub-images inside composite containers).
    /// </summary>
    public class ImageData
    {
        public readonly byte[] Raw;
        public readonly long Offset;
        public readonly long Length;

        public ImageData(byte[] Raw) : this(Raw, 0, Raw.Length)
        {
        }

        public ImageData(byte[] Raw, long Offset) : this(Raw, Offset, Raw.Length - Offset)
        {
        }

        public ImageData(byte[] Raw, long Offset, long Length)
        {
            this.Raw = Raw;
            this.Offset = Offset;
            this.Length = Length;
        }

        public byte ReadByte(long Index) => Raw[Offset + Index];

        public uint ReadUInt16(long Index) => Raw.ReadUInt16(Offset + Index);

        public uint ReadUInt16BE(long Index) => Raw.ReadUInt16BE(Offset + Index);

        public uint ReadUInt32(long Index) => Raw.ReadUInt32(Offset + Index);

        public uint ReadUInt32BE(long Index) => Raw.ReadUInt32BE(Offset + Index);

        public byte[] ReadArray(long Index, long Size)
        {
            var Res = new byte[Size];
            Array.Copy(Raw, Offset + Index, Res, 0, Size);
            return Res;
        }
    }
}
