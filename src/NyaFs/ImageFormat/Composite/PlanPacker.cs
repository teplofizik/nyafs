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
            Error = null;

            if (TotalSize <= 0)
            {
                Error = "Image size must be positive";
                return null;
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
                    return null;
                }

                if (Data.Length > Size)
                {
                    Error = $"Slot '{Slot.Name}' is too small for its content ({Size} < {Data.Length} bytes)";
                    return null;
                }

                var Offset = Slot.Offset ?? RoundUp(Cursor, Align);
                if (Offset < 0)
                {
                    Error = $"Slot '{Slot.Name}' has a negative offset";
                    return null;
                }

                var Count = Math.Max(1, Slot.RepeatCount);
                var Stride = Slot.RepeatStride ?? Size;

                for (int I = 0; I < Count; I++)
                {
                    var CopyOffset = Offset + I * Stride;

                    if ((CopyOffset < 0) || (CopyOffset + Size > TotalSize))
                    {
                        Error = $"Slot '{Slot.Name}' does not fit into the image (offset 0x{CopyOffset:x}, size {Size})";
                        return null;
                    }

                    foreach (var Range in Ranges)
                    {
                        if ((CopyOffset < Range.Item1 + Range.Item2) && (Range.Item1 < CopyOffset + Size))
                        {
                            Error = $"Slot '{Slot.Name}' overlaps '{Range.Item3}' at 0x{CopyOffset:x}";
                            return null;
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
                return null;
            }

            var Result = new byte[TotalSize];
            for (long I = 0; I < Result.LongLength; I++)
                Result[I] = DefaultFill;

            foreach (var P in Placed)
            {
                var Slot = P.Item1;
                var Offset = P.Item2;
                var Size = P.Item3;
                var Data = Slot.Data ?? Array.Empty<byte>();

                if (Data.Length > 0)
                    Buffer.BlockCopy(Data, 0, Result, (int)Offset, Data.Length);

                for (long I = Data.Length; I < Size; I++)
                    Result[Offset + I] = Slot.Fill;
            }

            return Result;
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
