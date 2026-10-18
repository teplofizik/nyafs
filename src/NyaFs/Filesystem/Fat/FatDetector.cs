using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Fat
{
    /// <summary>
    /// FAT12/FAT16/FAT32 boot-sector (BPB) detection and type classification.
    /// </summary>
    public static class FatDetector
    {
        public static bool IsFat(byte[] Raw, long Offset = 0)
        {
            if (Raw.Length - Offset < 512)
                return false;

            // Boot sector signature
            if (Raw[Offset + 510] != 0x55 || Raw[Offset + 511] != 0xAA)
                return false;

            byte jmp = Raw[Offset];
            if ((jmp != 0xEB) && (jmp != 0xE9))
                return false;

            uint bytsPerSec = Raw.ReadUInt16(Offset + 11);
            byte secPerClus = Raw[Offset + 13];
            uint rsvdSec = Raw.ReadUInt16(Offset + 14);
            byte numFats = Raw[Offset + 16];
            uint totSec16 = Raw.ReadUInt16(Offset + 19);
            uint fatSz16 = Raw.ReadUInt16(Offset + 22);
            uint totSec32 = Raw.ReadUInt32(Offset + 32);
            uint fatSz32 = Raw.ReadUInt32(Offset + 36);

            if ((bytsPerSec != 512) && (bytsPerSec != 1024) && (bytsPerSec != 2048) && (bytsPerSec != 4096))
                return false;

            if ((secPerClus == 0) || ((secPerClus & (secPerClus - 1)) != 0))
                return false;

            if ((numFats < 1) || (rsvdSec == 0))
                return false;

            if ((totSec16 == 0) && (totSec32 == 0))
                return false;

            if ((fatSz16 == 0) && (fatSz32 == 0))
                return false;

            return true;
        }

        public static int DetectFatType(int TotalSectors, int FirstDataSector, int SecPerClus, bool HasFat16)
        {
            if (!HasFat16)
                return 32;

            int clusters = (TotalSectors - FirstDataSector) / SecPerClus;
            return (clusters < 4085) ? 12 : 16;
        }

        public static string GetFatTypeName(int FatType) => $"FAT{FatType}";
    }
}
