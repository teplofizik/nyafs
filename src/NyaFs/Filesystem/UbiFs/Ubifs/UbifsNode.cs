using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.UbiFs.Ubifs
{
    /// <summary>
    /// UBIFS node types (on-flash, from ubifs-media.h).
    /// </summary>
    public enum UbifsNodeType
    {
        Ino = 0,
        Data = 1,
        Dent = 2,
        Xent = 3,
        Trun = 4,
        Pad = 5,
        Sb = 6,
        Mst = 7,
        Ref = 8,
        Idx = 9,
        Cs = 10,
        Orph = 11
    }

    public enum UbifsKeyType
    {
        Ino = 0,
        Data = 1,
        Dent = 2,
        Xent = 3
    }

    /// <summary>
    /// UBIFS simple-format key (8 bytes): u32[0] = inode (parent for dent/xent),
    /// u32[1] = (type &lt;&lt; 29) | block (or hash).
    /// </summary>
    public struct UbifsKey
    {
        public uint Inum;
        public uint W1;

        public UbifsKey(uint Inum, uint W1)
        {
            this.Inum = Inum;
            this.W1 = W1;
        }

        public UbifsKeyType Type => (UbifsKeyType)(W1 >> 29);
        public uint Block => W1 & 0x1FFFFFFF;
        public uint Hash => W1 & 0x1FFFFFFF;

        public static UbifsKey Read(byte[] D, int Off) => new UbifsKey(BitConverter.ToUInt32(D, Off), BitConverter.ToUInt32(D, Off + 4));

        public override string ToString() => $"{Type} inum={Inum} val={(Type == UbifsKeyType.Data ? Block.ToString() : Hash.ToString("x8"))}";
    }

    /// <summary>
    /// A UBIFS node: the underlying LEB buffer, the node offset within it and the parsed header.
    /// </summary>
    public class UbifsNode
    {
        public const uint MAGIC = 0x06101831;
        public const int HeaderSize = 24;

        public readonly byte[] Data;   // LEB buffer
        public readonly int Off;       // offset of the node within Data
        public readonly uint Lnum;
        public readonly UbifsKey Key;
        public readonly UbifsNodeType Type;

        public UbifsNode(byte[] D, int Off, uint Lnum, bool WithKey)
        {
            Data = D;
            this.Off = Off;
            this.Lnum = Lnum;
            Type = (UbifsNodeType)D[Off + 0x14];
            Key = WithKey ? UbifsKey.Read(D, Off + HeaderSize) : default;
        }

        public static bool IsValidMagic(byte[] D, int Off) =>
            (Off + 4 <= D.Length) && (BitConverter.ToUInt32(D, Off) == MAGIC);

        public uint Len => BitConverter.ToUInt32(Data, Off + 0x10);

        public override string ToString() => $"{Type} key={Key} lnum={Lnum} off={Off:x}";
    }

    /// <summary>
    /// UBIFS indexing node (ubifs_idx_node): ch + child_cnt(u16) + level(u16) + branches[].
    /// Each branch is lnum(u32) + offs(u32) + len(u32) + key(8).
    /// </summary>
    public class UbifsIndexNode : UbifsNode
    {
        public const int BranchSize = 12 + 8;

        public readonly int ChildCnt;
        public readonly int Level;

        public UbifsIndexNode(byte[] D, int Off, uint Lnum) : base(D, Off, Lnum, false)
        {
            ChildCnt = BitConverter.ToUInt16(D, Off + 0x18);
            Level = BitConverter.ToUInt16(D, Off + 0x1A);
        }

        public void GetBranch(int Index, out uint Lnum, out uint Offs, out uint Len, out UbifsKey Key)
        {
            int Br = Off + 0x1C + Index * BranchSize;
            Lnum = BitConverter.ToUInt32(Data, Br);
            Offs = BitConverter.ToUInt32(Data, Br + 4);
            Len = BitConverter.ToUInt32(Data, Br + 8);
            Key = UbifsKey.Read(Data, Br + 12);
        }
    }
}
