using NyaIO.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace NyaFs.Filesystem.UbiFs.Types
{
    internal class PhysicalBlock : ArrayWrapper
    {
        private List<VolumeTableRecord> VolumeRecords = new List<VolumeTableRecord>();

        int BlockSize;

        /* The maximum number of volumes per one UBI device */
        const uint UBI_MAX_VOLUMES = 128;

        public PhysicalBlock(byte[] Data, long Offset, int PhySize, int BlockSize) : base(Data, Offset, PhySize)
        {
            this.BlockSize = BlockSize;

            if(Correct)
                LoadVolumeTable();
        }

        public bool Correct => EraseCounterHeader.IsCorrect && VolumeIdentifierHeader.IsCorrect;

        private VolumeTableRecord ReadRecord(int Index)
        {
            // TODO
            int Offset = BlockSize * 2 + Index * VolumeTableRecord.Size;

            return new VolumeTableRecord(Raw, getOffset() + Offset);
        }

        private void LoadVolumeTable()
        {
            for (int i = 0; i < UBI_MAX_VOLUMES; i++)
            {
                var VTR = ReadRecord(i);

                if (VTR.IsCorrect)
                {
                    if (VTR.NameLength == 0)
                        break;
                    else
                        VolumeRecords.Add(VTR);
                }
            }
        }

        public void Dump()
        {
            var EC = EraseCounterHeader;
            var VI = VolumeIdentifierHeader;

            if (Correct)
            {
                Debug.WriteLine($"BLOCK at {getOffset():X08} SEQ {EC.ImageSequence:X08} DS {EC.DataSize} CNT {EC.EraseCounter}");
                Debug.WriteLine($"VH COMPAT {VI.Compat} TY {VI.VolumeType} ID {VI.VolumeId:X08} USED {VI.UsedEBs} LN {VI.LNum}");
                if(VolumeRecords.Count > 0)
                {
                    Debug.WriteLine($"  VOLUMES: {VolumeRecords.Count}");
                    for(int i = 0; i <  VolumeRecords.Count; i++)
                    {
                        Debug.WriteLine($"    VOLUME: {VolumeRecords[i].Name} T {VolumeRecords[i].VolumeType}");
                    }
                }

            }
            else
            {
                Debug.WriteLine($"INVALID BLOCK at {getOffset():X08}");
                if(EC.IsCorrect)
                    Debug.WriteLine($" EC: OK");
                if (VI.IsCorrect)
                    Debug.WriteLine($" VI: OK");

            }
        }

        public Types.EraseCounterHeader EraseCounterHeader => new Types.EraseCounterHeader(Raw, getOffset(), BlockSize);
        public Types.VolumeIdentifierHeader VolumeIdentifierHeader => new Types.VolumeIdentifierHeader(Raw, getOffset() + EraseCounterHeader.VidHeaderOffset, BlockSize);

    }
}
