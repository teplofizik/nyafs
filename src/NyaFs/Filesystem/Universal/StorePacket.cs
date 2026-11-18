using System;
using System.Buffers.Binary;
using System.Text;
using System.IO;

namespace NyaFs.Filesystem.Universal
{
    /// <summary>
    /// Byte-buffer-compatible packet over an IRandomAccessStore.
    /// Used by metadata-heavy filesystems (ext2/3/4) that need random access
    /// without keeping the whole image in memory.
    /// </summary>
    public class StorePacket
    {
        protected readonly IRandomAccessStore Store;
        protected readonly long BaseOffset;

        public StorePacket(IRandomAccessStore Store) : this(Store, 0) { }

        public StorePacket(IRandomAccessStore Store, long BaseOffset)
        {
            this.Store = Store ?? throw new ArgumentNullException(nameof(Store));
            this.BaseOffset = BaseOffset;
        }

        public long Length => Store.Length - BaseOffset;

        public virtual int getLength() => Convert.ToInt32(Length);

        public virtual byte[] getPacket()
        {
            var Result = new byte[Length];
            Store.Read(BaseOffset, Result, 0, Result.Length);
            return Result;
        }

        protected long O(long Offset) => BaseOffset + Offset;

        public void CopyTo(Stream Output)
        {
            var Buffer = new byte[Math.Min(Length, 0x100000)];
            long Pos = 0;
            while (Pos < Length)
            {
                var N = (int)Math.Min(Buffer.Length, Length - Pos);
                Store.Read(BaseOffset + Pos, Buffer, 0, N);
                Output.Write(Buffer, 0, N);
                Pos += N;
            }
        }

        public void Fill(byte Value)
        {
            var Buffer = new byte[Math.Min(Length, 0x10000)];
            for (int i = 0; i < Buffer.Length; i++) Buffer[i] = Value;
            long Pos = 0;
            while (Pos < Length)
            {
                var N = (int)Math.Min(Buffer.Length, Length - Pos);
                Store.Write(BaseOffset + Pos, Buffer, 0, N);
                Pos += N;
            }
        }

        public byte ReadByte(long Offset)
        {
            var B = new byte[1];
            Store.Read(O(Offset), B, 0, 1);
            return B[0];
        }

        public long WriteByte(long Offset, int Value) { return WriteByte(Offset, (byte)(Value & 0xFF)); }
        public long WriteByte(long Offset, byte Value) { var B = new[] { Value }; Store.Write(O(Offset), B, 0, 1); return Offset + 1; }

        public byte[] ReadArray(long Offset, int Length)
        {
            var Result = new byte[Length];
            Store.Read(O(Offset), Result, 0, Length);
            return Result;
        }

        public long WriteArray(long Offset, byte[] Data, int Length)
        {
            Store.Write(O(Offset), Data, 0, Length);
            return Offset + Length;
        }
        public long WriteArray(long Offset, byte[] Data) => WriteArray(Offset, Data, Data.Length);
        public long WriteArray(long Offset, byte[] Data, uint Length) => WriteArray(Offset, Data, (int)Length);
        public byte[] ReadArray(long Offset, long Length) => ReadArray(Offset, (int)Length);
        public byte[] ReadArray(long Offset, uint Length) => ReadArray(Offset, (int)Length);

        public ushort ReadUInt16(long Offset) { var B = new byte[2]; Store.Read(O(Offset), B, 0, 2); return BinaryPrimitives.ReadUInt16LittleEndian(B); }
        public uint ReadUInt32(long Offset) { var B = new byte[4]; Store.Read(O(Offset), B, 0, 4); return BinaryPrimitives.ReadUInt32LittleEndian(B); }
        public ulong ReadUInt64(long Offset) { var B = new byte[8]; Store.Read(O(Offset), B, 0, 8); return BinaryPrimitives.ReadUInt64LittleEndian(B); }
        public ushort ReadUInt16BE(long Offset) { var B = new byte[2]; Store.Read(O(Offset), B, 0, 2); return BinaryPrimitives.ReadUInt16BigEndian(B); }
        public uint ReadUInt32BE(long Offset) { var B = new byte[4]; Store.Read(O(Offset), B, 0, 4); return BinaryPrimitives.ReadUInt32BigEndian(B); }
        public ulong ReadUInt64BE(long Offset) { var B = new byte[8]; Store.Read(O(Offset), B, 0, 8); return BinaryPrimitives.ReadUInt64BigEndian(B); }

        private long Write(long Offset, ReadOnlySpan<byte> Data)
        {
            var B = Data.ToArray();
            Store.Write(O(Offset), B, 0, B.Length);
            return Offset + B.Length;
        }

        public long WriteUInt16(long Offset, int Value) => WriteUInt16(Offset, (uint)Value);
        public long WriteUInt16(long Offset, uint Value) { Span<byte> B = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(B, (ushort)Value); return Write(Offset, B); }
        public long WriteUInt32(long Offset, uint Value) { Span<byte> B = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(B, Value); return Write(Offset, B); }
        public long WriteUInt64(long Offset, ulong Value) { Span<byte> B = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(B, Value); return Write(Offset, B); }
        public long WriteUInt16BE(long Offset, int Value) => WriteUInt16BE(Offset, (uint)Value);
        public long WriteUInt16BE(long Offset, uint Value) { Span<byte> B = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(B, (ushort)Value); return Write(Offset, B); }
        public long WriteUInt32BE(long Offset, uint Value) { Span<byte> B = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(B, Value); return Write(Offset, B); }
        public long WriteUInt64BE(long Offset, ulong Value) { Span<byte> B = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(B, Value); return Write(Offset, B); }

        public uint[] ReadUInt32Array(long Offset, int Count)
        {
            var Result = new uint[Count];
            for (int i = 0; i < Count; i++) Result[i] = ReadUInt32(Offset + i * 4);
            return Result;
        }

        public uint[] ReadUInt32Array(long Offset, uint Count) => ReadUInt32Array(Offset, (int)Count);

        public long WriteUInt32Array(long Offset, uint[] Data)
        {
            for (int i = 0; i < Data.Length; i++) WriteUInt32(Offset + i * 4, Data[i]);
            return Offset + Data.Length * 4;
        }

        public string ReadANSIString(long Offset, int Length)
        {
            var B = ReadArray(Offset, Length);
            var Z = Array.IndexOf(B, (byte)0);
            return Encoding.ASCII.GetString(B, 0, Z < 0 ? B.Length : Z);
        }

        public long WriteANSIString(long Offset, string Text, int Length)
        {
            var B = new byte[Length];
            var S = Encoding.ASCII.GetBytes(Text ?? "");
            Array.Copy(S, B, Math.Min(S.Length, B.Length));
            Store.Write(O(Offset), B, 0, B.Length);
            return Offset + Length;
        }

        public string ReadString(long Offset, int Length)
        {
            var B = ReadArray(Offset, Length);
            var Z = Array.IndexOf(B, (byte)0);
            return Encoding.UTF8.GetString(B, 0, Z < 0 ? B.Length : Z);
        }

        public long WriteString(long Offset, string Text, int Length)
        {
            var B = new byte[Length];
            var S = Encoding.UTF8.GetBytes(Text ?? "");
            Array.Copy(S, B, Math.Min(S.Length, B.Length));
            Store.Write(O(Offset), B, 0, B.Length);
            return Offset + Length;
        }
    }

    /// <summary>
    /// Offset/length window over a store, analogous to NyaIO ArrayWrapper.
    /// </summary>
    public class StoreArrayWrapper : StorePacket
    {
        public StoreArrayWrapper(IRandomAccessStore Store, long Offset, long Length)
            : base(Store, Offset)
        {
            this.WindowLength = Length;
        }

        private readonly long WindowLength;
        public new long Length => WindowLength;
    }
}
