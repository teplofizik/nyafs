using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NyaFs.FlattenedDeviceTree.Types;

namespace NyaFs.ImageFormat.Elements.Dtb.Writer
{
    /// <summary>
    /// Device tree source (.dts) writer (dtc -I dtb -O dts style, no labels/phandles).
    /// </summary>
    public class DtsWriter : Writer
    {
        string Filename;
        byte[] Data = null;

        public DtsWriter()
        {

        }

        public DtsWriter(string Filename)
        {
            this.Filename = Filename;
        }

        public override void WriteDevtree(DeviceTree Dtb)
        {
            var Sb = new StringBuilder();
            Sb.Append("/dts-v1/;\n");

            var Reserve = Dtb.DevTree.ReserveMemory;
            if (Reserve.Length > 0)
            {
                Sb.Append('\n');
                foreach (var M in Reserve)
                    Sb.Append($"/memreserve/ 0x{M.Address:x} 0x{M.Size:x};\n");
            }

            Sb.Append('\n');
            WriteNode(Sb, Dtb.DevTree.Root, "", true);

            var Bytes = Encoding.UTF8.GetBytes(Sb.ToString());

            if (Filename != null)
            {
                Data = null;
                System.IO.File.WriteAllBytes(Filename, Bytes);
            }
            else
                Data = Bytes;
        }

        public override byte[] RawStream => Data;

        private static void WriteNode(StringBuilder Sb, Node N, string Indent, bool IsRoot)
        {
            if (IsRoot)
                Sb.Append("/ {\n");
            else
                Sb.Append($"{Indent}{N.Name} {{\n");

            var Inner = Indent + "\t";

            foreach (var P in N.Properties)
            {
                var V = FormatValue(P.Value);
                if (V.Length == 0)
                    Sb.Append($"{Inner}{P.Name};\n");
                else
                    Sb.Append($"{Inner}{P.Name} = {V};\n");
            }

            foreach (var C in N.Nodes)
                WriteNode(Sb, C, Inner, false);

            Sb.Append($"{Indent}}};\n");
        }

        /// <summary>
        /// Heuristic value formatting like dtc: NUL-terminated printable text -> string(s),
        /// 4-byte aligned -> cell list, otherwise a byte array.
        /// </summary>
        private static string FormatValue(byte[] V)
        {
            if ((V == null) || (V.Length == 0))
                return "";

            if (IsText(V))
                return FormatStrings(V);

            if ((V.Length % 4) == 0)
            {
                var Cells = new List<string>();
                for (int i = 0; i < V.Length; i += 4)
                {
                    uint C = (uint)((V[i] << 24) | (V[i + 1] << 16) | (V[i + 2] << 8) | V[i + 3]);
                    Cells.Add("0x" + C.ToString("x", CultureInfo.InvariantCulture));
                }
                return "<" + string.Join(" ", Cells) + ">";
            }

            var Bytes = new StringBuilder("[");
            for (int i = 0; i < V.Length; i++)
            {
                if (i > 0) Bytes.Append(' ');
                Bytes.Append(V[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            Bytes.Append(']');
            return Bytes.ToString();
        }

        private static bool IsText(byte[] V)
        {
            if (V[V.Length - 1] != 0)
                return false;

            int Start = 0;
            for (int i = 0; i < V.Length; i++)
            {
                if (V[i] != 0)
                    continue;

                if (i > Start)
                {
                    // non-empty chunk must be printable ASCII
                    for (int k = Start; k < i; k++)
                        if ((V[k] < 0x20) || (V[k] > 0x7E))
                            return false;
                }
                else if (i != V.Length - 1)
                {
                    // an empty chunk is only allowed as the final NUL terminator
                    return false;
                }

                Start = i + 1;
            }

            return true;
        }

        private static string FormatStrings(byte[] V)
        {
            var Parts = new List<string>();
            int Start = 0;
            for (int i = 0; i < V.Length; i++)
            {
                if (V[i] == 0)
                {
                    Parts.Add(Escape(Encoding.ASCII.GetString(V, Start, i - Start)));
                    Start = i + 1;
                }
            }

            // drop a trailing empty part (final NUL)
            if ((Parts.Count > 0) && (Parts[Parts.Count - 1].Length == 0))
                Parts.RemoveAt(Parts.Count - 1);

            return string.Join(", ", Parts.ConvertAll(P => "\"" + P + "\""));
        }

        private static string Escape(string S) => S.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
