using System;
using NyaFs.Filesystem.Universal;

namespace NyaFs.Filesystem.UbiFs.Ubifs
{
    /// <summary>
    /// LEB-addressable view over a random-access store. LEB n starts at n * LebSize.
    /// </summary>
    public sealed class LebStore
    {
        public readonly IRandomAccessStore Store;
        public readonly int LebSize;
        public readonly int LebCount;

        public LebStore(IRandomAccessStore Store, int LebSize, int LebCount)
        {
            this.Store = Store ?? throw new ArgumentNullException(nameof(Store));
            this.LebSize = LebSize;
            this.LebCount = LebCount;
        }

        public long Length => (long)LebSize * LebCount;

        public void Read(int Lnum, int Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            if ((Lnum < 0) || (Lnum >= LebCount)) throw new ArgumentOutOfRangeException(nameof(Lnum));
            if ((Offset < 0) || (Offset + Count > LebSize)) throw new ArgumentOutOfRangeException(nameof(Offset));
            Store.Read((long)Lnum * LebSize + Offset, Buffer, BufferOffset, Count);
        }

        public void Write(int Lnum, int Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            if ((Lnum < 0) || (Lnum >= LebCount)) throw new ArgumentOutOfRangeException(nameof(Lnum));
            if ((Offset < 0) || (Offset + Count > LebSize)) throw new ArgumentOutOfRangeException(nameof(Offset));
            Store.Write((long)Lnum * LebSize + Offset, Buffer, BufferOffset, Count);
        }

        public void Write(int Lnum, int Offset, byte[] Buffer) => Write(Lnum, Offset, Buffer, 0, Buffer.Length);

        public byte[] Read(int Lnum, int Offset, int Count)
        {
            var R = new byte[Count];
            Read(Lnum, Offset, R, 0, Count);
            return R;
        }

        public void Pad(int Lnum, int Offset, byte[] Pad) => Write(Lnum, Offset, Pad);
    }
}
