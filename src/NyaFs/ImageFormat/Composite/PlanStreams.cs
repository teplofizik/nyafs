using System;
using System.IO;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// A view over a region of a seekable output stream. Logical offset 0 maps to the
    /// region start, so a filesystem writer can lay out its image inside a slot while
    /// the surrounding container keeps writing sequentially.
    /// </summary>
    public sealed class RegionStream : Stream
    {
        private readonly Stream Base;
        private readonly long Start;
        private long PositionValue;
        private long LengthValue;
        private long WrittenValue;

        public RegionStream(Stream Base, long Start)
        {
            this.Base = Base ?? throw new ArgumentNullException(nameof(Base));
            this.Start = Start;
        }

        public long Written => WrittenValue;

        public override bool CanRead => Base.CanRead;
        public override bool CanSeek => Base.CanSeek;
        public override bool CanWrite => true;

        public override long Length => LengthValue;

        public override long Position
        {
            get => PositionValue;
            set => PositionValue = value;
        }

        public override void Flush() => Base.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!CanSeek)
                throw new NotSupportedException();

            Base.Seek(Start + PositionValue, SeekOrigin.Begin);
            int Read = Base.Read(buffer, offset, count);
            PositionValue += Read;
            return Read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek)
                throw new NotSupportedException();

            switch (origin)
            {
                case SeekOrigin.Begin: PositionValue = offset; break;
                case SeekOrigin.Current: PositionValue += offset; break;
                case SeekOrigin.End: PositionValue = LengthValue + offset; break;
            }

            return PositionValue;
        }

        public override void SetLength(long value)
        {
            LengthValue = value;
            if (Base.CanSeek && (Start + value > Base.Length))
                Base.SetLength(Start + value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Base.CanSeek)
                Base.Seek(Start + PositionValue, SeekOrigin.Begin);

            Base.Write(buffer, offset, count);
            PositionValue += count;

            if (PositionValue > LengthValue)
                LengthValue = PositionValue;
            if (PositionValue > WrittenValue)
                WrittenValue = PositionValue;
        }
    }
}
