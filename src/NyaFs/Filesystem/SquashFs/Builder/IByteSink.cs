using System;
using System.Collections.Generic;
using System.IO;

namespace NyaFs.Filesystem.SquashFs.Builder
{
    internal interface IByteSink
    {
        long Count { get; }
        void AddRange(byte[] Data);
        void AddRange(List<byte> Data);
        byte[] ReadAt(long Offset, int Length);
        void WriteAt(long Offset, byte[] Data);
        byte[] ToArray();
    }

    internal sealed class ListSink : IByteSink
    {
        private readonly List<byte> Data = new List<byte>();
        public long Count => Data.Count;
        public void AddRange(byte[] D) => Data.AddRange(D);
        public void AddRange(List<byte> D) => Data.AddRange(D);
        public byte[] ReadAt(long Offset, int Length) { var R = new byte[Length]; Data.CopyTo((int)Offset, R, 0, Length); return R; }
        public void WriteAt(long Offset, byte[] D) { for (int i = 0; i < D.Length; i++) Data[(int)Offset + i] = D[i]; }
        public byte[] ToArray() => Data.ToArray();
    }

    internal sealed class StreamSink : IByteSink
    {
        private readonly Stream Out;
        private long Pos;
        public StreamSink(Stream Out) { this.Out = Out; }
        public long Count => Pos;
        public void AddRange(byte[] D) { Out.Seek(Pos, SeekOrigin.Begin); Out.Write(D, 0, D.Length); Pos += D.Length; }
        public void AddRange(List<byte> D) => AddRange(D.ToArray());
        public byte[] ReadAt(long Offset, int Length) { var R = new byte[Length]; Out.Seek(Offset, SeekOrigin.Begin); var N = 0; while (N < Length) { var R2 = Out.Read(R, N, Length - N); if (R2 <= 0) throw new EndOfStreamException(); N += R2; } return R; }
        public void WriteAt(long Offset, byte[] D) { Out.Seek(Offset, SeekOrigin.Begin); Out.Write(D, 0, D.Length); }
        public byte[] ToArray() => throw new NotSupportedException();
    }
}
