using NyaFs.ImageFormat.Types;
using System;
using System.Collections.Generic;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// U-Boot legacy script and multi-file images (IH_TYPE_SCRIPT / IH_TYPE_MULTI).
    /// A multi image payload starts with big-endian sizes, a zero terminator, then data.
    /// </summary>
    public static class LegacyMultiImageWriter
    {
        public static byte[] BuildScript(byte[] Script, string Name = "script")
        {
            var Info = new ImageInfo
            {
                Type = ImageType.IH_TYPE_SCRIPT,
                Architecture = CPU.IH_ARCH_ARM,
                OperatingSystem = OS.IH_OS_LINUX,
                Compression = CompressionType.IH_COMP_NONE,
                Name = Name ?? "script"
            };

            var Data = BuildTable(new[] { Script });
            return new LegacyImage(Info, CompressionType.IH_COMP_NONE, Data).getPacket();
        }

        public static byte[] BuildMulti(IReadOnlyList<byte[]> Payloads, ImageInfo SourceInfo = null,
            string Name = "multi")
        {
            if ((Payloads == null) || (Payloads.Count == 0))
                return null;

            var Info = (SourceInfo != null) ? SourceInfo.Clone() : new ImageInfo();
            Info.Type = ImageType.IH_TYPE_MULTI;
            Info.Compression = CompressionType.IH_COMP_NONE;
            Info.Name = String.IsNullOrEmpty(Name) ? "multi" : Name;
            if (Info.Architecture == CPU.IH_ARCH_INVALID)
                Info.Architecture = CPU.IH_ARCH_ARM;
            if (Info.OperatingSystem == OS.IH_OS_INVALID)
                Info.OperatingSystem = OS.IH_OS_LINUX;

            var Data = BuildTable(Payloads);
            return new LegacyImage(Info, CompressionType.IH_COMP_NONE, Data).getPacket();
        }

        private static byte[] BuildTable(IReadOnlyList<byte[]> Payloads)
        {
            var Size = 4 * Payloads.Count + 4;
            for (var I = 0; I < Payloads.Count; I++)
            {
                var Length = Payloads[I]?.Length ?? 0;
                Size += Length;
                if (I + 1 < Payloads.Count)
                    Size += Align4(Length) - Length;
            }

            var Result = new byte[Size];
            var Position = 0;
            foreach (var Payload in Payloads)
            {
                var Length = Payload?.Length ?? 0;
                WriteU32(Result, Position, (uint)Length);
                Position += 4;
            }
            WriteU32(Result, Position, 0);
            Position += 4;

            for (var I = 0; I < Payloads.Count; I++)
            {
                var Payload = Payloads[I];
                if (Payload != null)
                {
                    Buffer.BlockCopy(Payload, 0, Result, Position, Payload.Length);
                    Position += Payload.Length;
                }
                if (I + 1 < Payloads.Count)
                    Position += Align4(Payload?.Length ?? 0) - (Payload?.Length ?? 0);
            }

            return Result;
        }

        private static int Align4(int Value) => (Value + 3) & ~3;

        private static void WriteU32(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)(Value >> 24);
            Data[Offset + 1] = (byte)(Value >> 16);
            Data[Offset + 2] = (byte)(Value >> 8);
            Data[Offset + 3] = (byte)Value;
        }
    }
}
