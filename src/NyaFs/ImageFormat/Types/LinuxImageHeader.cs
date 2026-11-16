using System;
using NyaIO.Data;

namespace NyaFs.ImageFormat.Types
{
    /// <summary>
    /// Header of a bare (uncompressed) Linux kernel Image used by arm64/riscv.
    /// Layout (little endian):
    ///   0x00 code0, 0x04 code1, 0x08 text_offset (u64), 0x10 image_size (u64),
    ///   0x18 flags (u64), 0x20 res2, 0x28 res3, 0x30 res4, 0x38 magic.
    /// arm64 magic is "ARM\x64", riscv magic is "RISCV\0\0\0".
    /// </summary>
    public class LinuxImageHeader
    {
        public const int HeaderSize = 0x40;

        private static readonly byte[] Arm64Magic = { 0x41, 0x52, 0x4D, 0x64 };
        private static readonly byte[] RiscVMagic = { 0x52, 0x49, 0x53, 0x43, 0x56, 0x00, 0x00, 0x00 };

        public bool IsValid { get; private set; }
        public bool IsArm64 { get; private set; }
        public bool IsRiscV { get; private set; }

        public CPU Architecture { get; private set; } = CPU.IH_ARCH_INVALID;
        public ulong TextOffset { get; private set; }
        public ulong ImageSize { get; private set; }
        public ulong Flags { get; private set; }

        /// <summary>Kernel page size in bytes (arm64 only; 0 when unknown).</summary>
        public int PageSize { get; private set; }

        public LinuxImageHeader(byte[] Data)
        {
            if ((Data == null) || (Data.Length < HeaderSize))
                return;

            if (Matches(Data, 0x38, Arm64Magic))
            {
                IsArm64 = true;
                Architecture = CPU.IH_ARCH_ARM64;
            }
            else if (Matches(Data, 0x38, RiscVMagic))
            {
                IsRiscV = true;
                Architecture = CPU.IH_ARCH_RISCV;
            }
            else
            {
                return;
            }

            IsValid = true;
            TextOffset = Data.ReadUInt64(0x08);
            ImageSize = Data.ReadUInt64(0x10);
            Flags = Data.ReadUInt64(0x18);

            if (IsArm64)
                PageSize = 4096 << (int)((Flags >> 1) & 0x3);
        }

        private static bool Matches(byte[] Data, int Offset, byte[] Magic)
        {
            if (Offset + Magic.Length > Data.Length)
                return false;

            for (var I = 0; I < Magic.Length; I++)
                if (Data[Offset + I] != Magic[I])
                    return false;

            return true;
        }
    }
}
