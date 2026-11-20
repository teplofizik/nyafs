using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NyaFs.FlattenedDeviceTree
{
    /// <summary>
    /// Parses and formats devicetree property values using a dtc-like syntax:
    /// <c>"text"</c>, <c>&lt;0x1 0x2&gt;</c> (big-endian cells) and <c>[00 ff]</c> (bytes).
    /// </summary>
    public static class FdtValue
    {
        /// <summary>Parse a value from the text (a bare number is treated as one cell).</summary>
        public static bool TryParse(string Text, out byte[] Value, out string Error)
        {
            Value = null;
            Error = null;

            var S = (Text ?? "").Trim();
            if (S.Length == 0)
            {
                Value = Array.Empty<byte>();
                return true;
            }

            if ((S[0] == '<') && (S[S.Length - 1] == '>'))
                return TryParseCells(S.Substring(1, S.Length - 2), out Value, out Error);

            if ((S[0] == '[') && (S[S.Length - 1] == ']'))
                return TryParseBytes(S.Substring(1, S.Length - 2), out Value, out Error);

            if (TryParseUInt32(S, out var Cell))
            {
                Value = new byte[4];
                WriteUInt32BE(Value, 0, Cell);
                return true;
            }

            Value = Encoding.UTF8.GetBytes(S + "\0");
            return true;
        }

        /// <summary>Format a value like dtc: string list, cell list or byte array.</summary>
        public static string Format(byte[] Value)
        {
            if ((Value == null) || (Value.Length == 0))
                return "";

            if (IsText(Value))
                return FormatStrings(Value);

            if ((Value.Length % 4) == 0)
            {
                var Cells = new List<string>();
                for (int i = 0; i < Value.Length; i += 4)
                    Cells.Add("0x" + ReadUInt32BE(Value, i).ToString("x", CultureInfo.InvariantCulture));
                return "<" + String.Join(" ", Cells) + ">";
            }

            var Bytes = new StringBuilder("[");
            for (int i = 0; i < Value.Length; i++)
            {
                if (i > 0) Bytes.Append(' ');
                Bytes.Append(Value[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            Bytes.Append(']');
            return Bytes.ToString();
        }

        public static uint ReadUInt32BE(byte[] Data, int Offset) =>
            (uint)((Data[Offset] << 24) | (Data[Offset + 1] << 16) | (Data[Offset + 2] << 8) | Data[Offset + 3]);

        public static void WriteUInt32BE(byte[] Data, int Offset, uint Value)
        {
            Data[Offset] = (byte)(Value >> 24);
            Data[Offset + 1] = (byte)(Value >> 16);
            Data[Offset + 2] = (byte)(Value >> 8);
            Data[Offset + 3] = (byte)Value;
        }

        private static bool TryParseCells(string Text, out byte[] Value, out string Error)
        {
            Value = null;
            Error = null;

            var Tokens = Text.Split(new char[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var Bytes = new byte[Tokens.Length * 4];
            for (int i = 0; i < Tokens.Length; i++)
            {
                var T = Tokens[i].Trim();
                if (T.StartsWith("&", StringComparison.Ordinal))
                {
                    Error = "Label references (&name) are not supported in values; use a numeric phandle";
                    return false;
                }

                if (!TryParseUInt32(T, out var Cell))
                {
                    Error = $"Cannot parse cell value '{T}'";
                    return false;
                }

                WriteUInt32BE(Bytes, i * 4, Cell);
            }

            Value = Bytes;
            return true;
        }

        private static bool TryParseBytes(string Text, out byte[] Value, out string Error)
        {
            Value = null;
            Error = null;

            var Tokens = Text.Split(new char[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var Bytes = new List<byte>(Tokens.Length);
            foreach (var RawToken in Tokens)
            {
                var T = RawToken.Trim();
                if (T.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    T = T.Substring(2);

                if ((T.Length == 0) || (T.Length > 2) ||
                    !byte.TryParse(T, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var B))
                {
                    Error = $"Cannot parse byte value '{RawToken}'";
                    return false;
                }

                Bytes.Add(B);
            }

            Value = Bytes.ToArray();
            return true;
        }

        private static bool TryParseUInt32(string Text, out uint Value)
        {
            if (Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return UInt32.TryParse(Text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out Value);

            return UInt32.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out Value);
        }

        private static bool IsText(byte[] Value)
        {
            if (Value[Value.Length - 1] != 0)
                return false;

            int Start = 0;
            for (int i = 0; i < Value.Length; i++)
            {
                if (Value[i] != 0)
                    continue;

                if (i > Start)
                {
                    for (int k = Start; k < i; k++)
                        if ((Value[k] < 0x20) || (Value[k] > 0x7E))
                            return false;
                }
                else if (i != Value.Length - 1)
                    return false;

                Start = i + 1;
            }

            return true;
        }

        private static string FormatStrings(byte[] Value)
        {
            var Parts = new List<string>();
            int Start = 0;
            for (int i = 0; i < Value.Length; i++)
            {
                if (Value[i] == 0)
                {
                    Parts.Add(Escape(Encoding.ASCII.GetString(Value, Start, i - Start)));
                    Start = i + 1;
                }
            }

            if ((Parts.Count > 0) && (Parts[Parts.Count - 1].Length == 0))
                Parts.RemoveAt(Parts.Count - 1);

            return String.Join(", ", Parts.ConvertAll(P => "\"" + P + "\""));
        }

        private static string Escape(string S) => S.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
