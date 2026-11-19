using System;
using System.IO;
using NyaFs.Filesystem.Universal;

namespace NyaFs.Filesystem.UbiFs
{
    /// <summary>
    /// Maps a logical LEB-addressed volume onto the data areas of consecutive PEBs in
    /// an already-allocated UBI container stream. This lets a UBIFS builder write its
    /// LEBs straight into their final PEB positions without keeping the volume (or the
    /// whole UBI image) in memory.
    /// </summary>
    public sealed class PebStore : IRandomAccessStore
    {
        private readonly Stream Stream;
        private readonly int PebSize;
        private readonly int DataOffset;
        private readonly int LebSize;
        private readonly int BasePeb;
        private readonly long StreamBaseOffset;
        private readonly long LengthValue;

        public PebStore(Stream Stream, int PebSize, int DataOffset, int LebSize, int BasePeb, long Length,
            long StreamBaseOffset = 0)
        {
            this.Stream = Stream ?? throw new ArgumentNullException(nameof(Stream));
            if (!Stream.CanSeek || !Stream.CanWrite)
                throw new ArgumentException("PebStore requires a seekable writable stream", nameof(Stream));

            this.PebSize = PebSize;
            this.DataOffset = DataOffset;
            this.LebSize = LebSize;
            this.BasePeb = BasePeb;
            this.StreamBaseOffset = StreamBaseOffset;
            this.LengthValue = Length;
        }

        public long Length => LengthValue;

        public void Read(long Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            while (Count > 0)
            {
                int InLeb = (int)(Offset % LebSize);
                int Chunk = Math.Min(Count, LebSize - InLeb);
                Stream.Seek(StreamBaseOffset + ((BasePeb + Offset / LebSize) * PebSize) + DataOffset + InLeb, SeekOrigin.Begin);

                int Read = 0;
                while (Read < Chunk)
                {
                    int N = Stream.Read(Buffer, BufferOffset + Read, Chunk - Read);
                    if (N <= 0) throw new EndOfStreamException();
                    Read += N;
                }

                Offset += Chunk;
                BufferOffset += Chunk;
                Count -= Chunk;
            }
        }

        public void Write(long Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            while (Count > 0)
            {
                int InLeb = (int)(Offset % LebSize);
                int Chunk = Math.Min(Count, LebSize - InLeb);
                Stream.Seek(StreamBaseOffset + ((BasePeb + Offset / LebSize) * PebSize) + DataOffset + InLeb, SeekOrigin.Begin);
                Stream.Write(Buffer, BufferOffset, Chunk);

                Offset += Chunk;
                BufferOffset += Chunk;
                Count -= Chunk;
            }
        }

        public void Dispose() => Stream.Flush();
    }
}
