using System;

namespace NyaFs.Filesystem.EroFs
{
    internal static class EroFsShared
    {
        public const int SuperblockOffset = 1024;
        public const uint Magic = 0xE0F5E1E2;

        public static bool IsEroFs(byte[] Data, long Offset = 0)
        {
            if (Data.Length < Offset + SuperblockOffset + 4)
                return false;

            return ReadU32(Data, (int)(Offset + SuperblockOffset)) == Magic;
        }

        public static uint ReadU16(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8));

        public static uint ReadU32(byte[] Data, int Offset) =>
            (uint)(Data[Offset] | (Data[Offset + 1] << 8) |
                   (Data[Offset + 2] << 16) | (Data[Offset + 3] << 24));

        public static ulong ReadU64(byte[] Data, int Offset) =>
            ReadU32(Data, Offset) | ((ulong)ReadU32(Data, Offset + 4) << 32);

        public static uint ReadU32(byte[] Data, long Offset) => ReadU32(Data, checked((int)Offset));
    }
}
