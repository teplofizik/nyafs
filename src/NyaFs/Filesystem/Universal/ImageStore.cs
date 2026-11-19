using System;

namespace NyaFs.Filesystem.Universal
{
    /// <summary>
    /// Byte-addressable image buffer backed by an <see cref="IRandomAccessStore"/>.
    /// Lets metadata-heavy builders write scattered structures straight to the output
    /// stream instead of allocating the whole image as a byte[].
    /// </summary>
    public sealed class ImageStore
    {
        private readonly IRandomAccessStore Store;

        public ImageStore(IRandomAccessStore Store)
        {
            this.Store = Store ?? throw new ArgumentNullException(nameof(Store));
        }

        public long Length => Store.Length;

        public byte this[long Index]
        {
            get
            {
                var B = new byte[1];
                Store.Read(Index, B, 0, 1);
                return B[0];
            }
            set
            {
                var B = new[] { value };
                Store.Write(Index, B, 0, 1);
            }
        }

        public void Write(long Offset, byte[] Data) => Store.Write(Offset, Data, 0, Data.Length);

        public void Write(long Offset, byte[] Data, int Count) => Store.Write(Offset, Data, 0, Count);

        public void Write(long Offset, byte[] Data, int SourceOffset, int Count) =>
            Store.Write(Offset, Data, SourceOffset, Count);

        public void U16(long Offset, uint Value)
        {
            var B = new byte[2];
            B[0] = (byte)Value;
            B[1] = (byte)(Value >> 8);
            Store.Write(Offset, B, 0, 2);
        }

        public void U32(long Offset, uint Value)
        {
            var B = new byte[4];
            B[0] = (byte)Value;
            B[1] = (byte)(Value >> 8);
            B[2] = (byte)(Value >> 16);
            B[3] = (byte)(Value >> 24);
            Store.Write(Offset, B, 0, 4);
        }

        public void U64(long Offset, ulong Value)
        {
            var B = new byte[8];
            for (int i = 0; i < 8; i++)
                B[i] = (byte)(Value >> (i * 8));
            Store.Write(Offset, B, 0, 8);
        }
    }
}
