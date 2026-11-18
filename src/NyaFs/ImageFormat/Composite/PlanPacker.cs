using System;
using System.Collections.Generic;
using NyaFs.ImageFormat;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>One piece of content to place into a layout.</summary>
    public class PlanSlot
    {
        public string Name = "";
        public long? Offset;
        public long? Size;
        public bool SizeAuto;
        public long? Align;
        public int RepeatCount = 1;
        public long? RepeatStride;
        public byte Fill = 0xFF;
        public byte[] Data;
    }

    /// <summary>
    /// Helper for "blobs at offsets" formats (NAND/MTD, disk, vendor flash layouts):
    /// place the given slots into one image, fill the gaps, honour alignment and
    /// repeated copies, and reject overlaps or overflow.
    /// </summary>
    public static class PlanPacker
    {
        public static byte[] Pack(long TotalSize, byte DefaultFill, long DefaultAlign,
            IEnumerable<PlanSlot> Slots, out string Error)
        {
            using (var Stream = new System.IO.MemoryStream())
            {
                if (!PackTo(Stream, TotalSize, DefaultFill, DefaultAlign, Slots, out Error))
                    return null;

                return Stream.ToArray();
            }
        }

        /// <summary>
        /// Stream slots into the output (sequential, no seeking). Payloads are still
        /// supplied as byte arrays, but the resulting image is not allocated.
        /// </summary>
        public static bool PackTo(System.IO.Stream Output, long TotalSize, byte DefaultFill, long DefaultAlign,
            IEnumerable<PlanSlot> Slots, out string Error)
        {
            Error = null;

            if ((Output == null) || !Output.CanWrite)
            {
                Error = "Output stream is not writable";
                return false;
            }

            if (TotalSize <= 0)
            {
                Error = "Image size must be positive";
                return false;
            }

            var Placed = new List<Tuple<PlanSlot, long, long>>();
            var Ranges = new List<Tuple<long, long, string>>();
            long Cursor = 0;

            foreach (var Slot in Slots)
            {
                var Align = (Slot.Align.HasValue && (Slot.Align.Value > 0)) ? Slot.Align.Value : Math.Max(1, DefaultAlign);
                var Data = Slot.Data ?? Array.Empty<byte>();

                long Size;
                if (Slot.SizeAuto || !Slot.Size.HasValue)
                    Size = Data.Length;
                else
                    Size = Slot.Size.Value;

                if (Size <= 0)
                {
                    Error = $"Slot '{Slot.Name}' has no size";
                    return false;
                }

                if (Data.Length > Size)
                {
                    Error = $"Slot '{Slot.Name}' is too small for its content ({Size} < {Data.Length} bytes)";
                    return false;
                }

                var Offset = Slot.Offset ?? RoundUp(Cursor, Align);
                if (Offset < 0)
                {
                    Error = $"Slot '{Slot.Name}' has a negative offset";
                    return false;
                }

                var Count = Math.Max(1, Slot.RepeatCount);
                var Stride = Slot.RepeatStride ?? Size;

                for (int I = 0; I < Count; I++)
                {
                    var CopyOffset = Offset + I * Stride;

                    if ((CopyOffset < 0) || (CopyOffset + Size > TotalSize))
                    {
                        Error = $"Slot '{Slot.Name}' does not fit into the image (offset 0x{CopyOffset:x}, size {Size})";
                        return false;
                    }

                    foreach (var Range in Ranges)
                    {
                        if ((CopyOffset < Range.Item1 + Range.Item2) && (Range.Item1 < CopyOffset + Size))
                        {
                            Error = $"Slot '{Slot.Name}' overlaps '{Range.Item3}' at 0x{CopyOffset:x}";
                            return false;
                        }
                    }

                    Ranges.Add(Tuple.Create(CopyOffset, Size, Slot.Name));
                    Placed.Add(Tuple.Create(Slot, CopyOffset, Size));
                }

                Cursor = Math.Max(Cursor, Offset + Count * Stride);
            }

            if (Cursor > TotalSize)
            {
                Error = "Layout exceeds the image size";
                return false;
            }

            Placed.Sort((A, B) => A.Item2.CompareTo(B.Item2));

            long Pos = 0;
            foreach (var P in Placed)
            {
                var Slot = P.Item1;
                var Offset = P.Item2;
                var Size = P.Item3;
                var Data = Slot.Data ?? Array.Empty<byte>();

                if (Offset < Pos)
                {
                    Error = $"Slot '{Slot.Name}' overlaps the previous data";
                    return false;
                }

                WriteFill(Output, Offset - Pos, DefaultFill);
                Pos = Offset;

                if (Data.Length > 0)
                {
                    Output.Write(Data, 0, Data.Length);
                    Pos += Data.Length;
                }

                WriteFill(Output, Size - Data.Length, Slot.Fill);
                Pos += Size - Data.Length;
            }

            WriteFill(Output, TotalSize - Pos, DefaultFill);
            return true;
        }

        private static void WriteFill(System.IO.Stream Output, long Count, byte Fill)
        {
            if (Count <= 0)
                return;

            var Buffer = new byte[64 * 1024];
            for (var I = 0; I < Buffer.Length; I++)
                Buffer[I] = Fill;

            while (Count > 0)
            {
                var Chunk = (int)Math.Min(Count, Buffer.Length);
                Output.Write(Buffer, 0, Chunk);
                Count -= Chunk;
            }
        }
        private static long RoundUp(long Value, long Align)
        {
            if (Align <= 1)
                return Value;
            return ((Value + Align - 1) / Align) * Align;
        }
    }

    /// <summary>Generic conversion of layout elements into build plan nodes.</summary>
    public static class PlanNodes
    {
        public static ImageBuildNode FromElement(PlanElement Element, string ParentPath = "")
        {
            var Node = new ImageBuildNode
            {
                Name = Element.Name,
                Path = (String.IsNullOrEmpty(ParentPath) ? Element.Name : ParentPath + "/" + Element.Name),
                Kind = Element.Kind,
                Format = Element.Format,
                Offset = Element.Offset,
                Size = Element.Size,
                SizeAuto = Element.SizeAuto,
                Align = Element.Align,
                RepeatCount = Element.RepeatCount,
                RepeatStride = Element.RepeatStride,
                Fill = Element.Fill,
                Immutable = Element.Immutable,
                Content = Element.Content
            };

            // Editable placeholder: lets `use`/`import`/`mkfs` work on the slot.
            Node.Raw = Array.Empty<byte>();

            foreach (var Option in Element.Options)
                Node.Options[Option.Key] = Option.Value;

            foreach (var Child in Element.Children)
                Node.Children.Add(FromElement(Child, Node.Path));

            return Node;
        }
    }
}
