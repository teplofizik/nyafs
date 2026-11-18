using System;
using System.IO;

namespace NyaFs.Filesystem.Universal
{
    /// <summary>
    /// Random-access storage used by builders that cannot keep the whole image in a
    /// byte[] (ext2/3/4 and other metadata-heavy filesystems).
    /// </summary>
    public interface IRandomAccessStore : IDisposable
    {
        long Length { get; }
        void Read(long Offset, byte[] Buffer, int BufferOffset, int Count);
        void Write(long Offset, byte[] Buffer, int BufferOffset, int Count);
    }

    public sealed class MemoryStore : IRandomAccessStore
    {
        private byte[] Data;

        public MemoryStore(long Length) { Data = new byte[Length]; }
        public MemoryStore(byte[] Data) { this.Data = Data; }

        public long Length => Data.LongLength;

        public void Read(long Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            if ((Offset < 0) || (Offset + Count > Data.LongLength))
                throw new ArgumentOutOfRangeException(nameof(Offset));
            Array.Copy(Data, Offset, Buffer, BufferOffset, Count);
        }

        public void Write(long Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            if ((Offset < 0) || (Offset + Count > Data.LongLength))
                throw new ArgumentOutOfRangeException(nameof(Offset));
            Array.Copy(Buffer, BufferOffset, Data, Offset, Count);
        }

        public byte[] ToArray() => (byte[])Data.Clone();
        public void Dispose() { }
    }

    public sealed class StreamStore : IRandomAccessStore
    {
        private readonly Stream Stream;
        private readonly long LengthValue;
        private readonly long BaseOffset;

        public StreamStore(Stream Stream, long Length, long BaseOffset = 0)
        {
            this.Stream = Stream ?? throw new ArgumentNullException(nameof(Stream));
            if (!Stream.CanSeek || !Stream.CanWrite)
                throw new ArgumentException("StreamStore requires a seekable writable stream", nameof(Stream));
            this.LengthValue = Length;
            this.BaseOffset = BaseOffset;
            Stream.SetLength(BaseOffset + Length);
        }

        public long Length => LengthValue;

        public void Read(long Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            Stream.Seek(BaseOffset + Offset, SeekOrigin.Begin);
            var Read = 0;
            while (Read < Count)
            {
                var N = Stream.Read(Buffer, BufferOffset + Read, Count - Read);
                if (N <= 0) throw new EndOfStreamException();
                Read += N;
            }
        }

        public void Write(long Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            Stream.Seek(BaseOffset + Offset, SeekOrigin.Begin);
            Stream.Write(Buffer, BufferOffset, Count);
        }

        public void Dispose() { Stream.Flush(); }
    }
    public sealed class FileStore : IRandomAccessStore
    {
        private readonly FileStream Stream;

        public FileStore(string Filename, long Length)
        {
            Stream = new FileStream(Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Stream.SetLength(Length);
        }

        public long Length => Stream.Length;

        public void Read(long Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            Stream.Seek(Offset, SeekOrigin.Begin);
            var Read = 0;
            while (Read < Count)
            {
                var N = Stream.Read(Buffer, BufferOffset + Read, Count - Read);
                if (N <= 0)
                    throw new EndOfStreamException();
                Read += N;
            }
        }

        public void Write(long Offset, byte[] Buffer, int BufferOffset, int Count)
        {
            Stream.Seek(Offset, SeekOrigin.Begin);
            Stream.Write(Buffer, BufferOffset, Count);
        }

        public void CopyTo(Stream Output)
        {
            Stream.Seek(0, SeekOrigin.Begin);
            Stream.CopyTo(Output);
        }

        public void Dispose() => Stream.Dispose();
    }
}
