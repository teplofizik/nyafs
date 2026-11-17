using System;
using System.Collections.Generic;
using System.Text;
using NyaIO.Data;

namespace NyaFs.Filesystem.Tar.Types
{
    /// <summary>
    /// USTAR header (512 bytes).
    /// https://www.gnu.org/software/tar/manual/html_node/Standard.html
    /// </summary>
    public class TarHeader : ArrayWrapper
    {
        public const int HeaderSize = 512;

        private const long NameOffset = 0;
        private const long ModeOffset = 100;
        private const long UidOffset = 108;
        private const long GidOffset = 116;
        private const long SizeOffset = 124;
        private const long MTimeOffset = 136;
        private const long ChecksumOffset = 148;
        private const long TypeFlagOffset = 156;
        private const long LinkNameOffset = 157;
        private const long MagicOffset = 257;
        private const long VersionOffset = 263;
        private const long UserNameOffset = 265;
        private const long GroupNameOffset = 297;
        private const long DevMajorOffset = 329;
        private const long DevMinorOffset = 337;
        private const long PrefixOffset = 345;

        public TarHeader() : base(HeaderSize) { }

        public TarHeader(byte[] Raw, long Offset) : base(Raw, Offset, HeaderSize) { }

        private byte GetByte(long Offset) => ReadArray(Offset, 1)[0];

        private void SetByte(long Offset, byte Value) => WriteArray(Offset, new byte[] { Value }, 1);

        private string ReadAscii(long Offset, int Size)
        {
            var Raw = ReadArray(Offset, Size);
            int Len = Array.IndexOf(Raw, (byte)0);
            if (Len < 0) Len = Size;
            return Encoding.ASCII.GetString(Raw, 0, Len);
        }

        private void WriteAscii(long Offset, string Value, int Size)
        {
            var Res = new byte[Size];
            var Data = Encoding.ASCII.GetBytes(Value ?? "");
            Array.Copy(Data, Res, Math.Min(Data.Length, Size));
            WriteArray(Offset, Res, Size);
        }

        /// <summary>
        /// Read a numeric field (octal ASCII, or GNU base-256 if the high bit is set).
        /// </summary>
        private long ReadNumber(long Offset, int Size)
        {
            var Raw = ReadArray(Offset, Size);

            if ((Raw[0] & 0x80) != 0)
            {
                long Res = 0;
                for (int i = 0; i < Size; i++)
                    Res = (Res << 8) | (Raw[i] & (long)((i == 0) ? 0x7F : 0xFF));
                return Res;
            }

            int Len = Size;
            for (int i = 0; i < Size; i++)
                if ((Raw[i] == 0) || (Raw[i] == 0x20)) { Len = i; break; }

            if (Len == 0) return 0;

            return Convert.ToInt64(Encoding.ASCII.GetString(Raw, 0, Len).Trim(), 8);
        }

        private void WriteNumber(long Offset, int Size, long Value)
        {
            var Res = new byte[Size];
            var Text = Convert.ToString(Value, 8).PadLeft(Size - 1, '0');
            var Data = Encoding.ASCII.GetBytes(Text);
            Array.Copy(Data, Res, Math.Min(Data.Length, Size - 1));
            WriteArray(Offset, Res, Size);
        }

        /// <summary>
        /// True if this header block is entirely zero (end-of-archive marker).
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < HeaderSize; i++)
                    if (GetByte(i) != 0) return false;
                return true;
            }
        }

        public string Name { get => ReadAscii(NameOffset, 100); set => WriteAscii(NameOffset, value, 100); }

        public long Mode { get => ReadNumber(ModeOffset, 8); set => WriteNumber(ModeOffset, 8, value); }

        public long Uid { get => ReadNumber(UidOffset, 8); set => WriteNumber(UidOffset, 8, value); }

        public long Gid { get => ReadNumber(GidOffset, 8); set => WriteNumber(GidOffset, 8, value); }

        public long Size { get => ReadNumber(SizeOffset, 12); set => WriteNumber(SizeOffset, 12, value); }

        public long MTime { get => ReadNumber(MTimeOffset, 12); set => WriteNumber(MTimeOffset, 12, value); }

        public byte TypeFlagByte { get => GetByte(TypeFlagOffset); set => SetByte(TypeFlagOffset, value); }

        public string LinkName { get => ReadAscii(LinkNameOffset, 100); set => WriteAscii(LinkNameOffset, value, 100); }

        public string Magic { get => ReadAscii(MagicOffset, 6); set => WriteAscii(MagicOffset, value, 6); }

        public string Version { get => ReadAscii(VersionOffset, 2); set => WriteAscii(VersionOffset, value, 2); }

        public string UserName { get => ReadAscii(UserNameOffset, 32); set => WriteAscii(UserNameOffset, value, 32); }

        public string GroupName { get => ReadAscii(GroupNameOffset, 32); set => WriteAscii(GroupNameOffset, value, 32); }

        public long DevMajor { get => ReadNumber(DevMajorOffset, 8); set => WriteNumber(DevMajorOffset, 8, value); }

        public long DevMinor { get => ReadNumber(DevMinorOffset, 8); set => WriteNumber(DevMinorOffset, 8, value); }

        public string Prefix { get => ReadAscii(PrefixOffset, 155); set => WriteAscii(PrefixOffset, value, 155); }

        /// <summary>
        /// Is ustar magic present.
        /// </summary>
        public bool IsUstar => ReadAscii(MagicOffset, 5) == "ustar";

        /// <summary>
        /// Full name combined from prefix and name.
        /// </summary>
        public string FullName
        {
            get
            {
                var P = Prefix;
                var N = Name;
                return (P.Length > 0) ? (P + "/" + N) : N;
            }
        }

        /// <summary>
        /// Checksum with the checksum field treated as spaces.
        /// </summary>
        private long CalcChecksum()
        {
            long Res = 0;
            for (int i = 0; i < HeaderSize; i++)
                Res += (i >= ChecksumOffset && i < ChecksumOffset + 8) ? 0x20 : GetByte(i);
            return Res;
        }

        /// <summary>
        /// Write the header checksum ("%06o\0 ").
        /// </summary>
        public void WriteChecksum()
        {
            var Sum = CalcChecksum();
            var Text = Convert.ToString(Sum, 8).PadLeft(6, '0');
            var Data = Encoding.ASCII.GetBytes(Text);

            var Res = new byte[8];
            Array.Copy(Data, Res, 6);
            Res[6] = 0;
            Res[7] = 0x20;
            WriteArray(ChecksumOffset, Res, 8);
        }
    }
}
