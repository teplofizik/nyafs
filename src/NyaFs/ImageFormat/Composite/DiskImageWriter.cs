using NyaFs;
using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Composite;
using System;
using System.Collections.Generic;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Writes a raw MBR/GPT disk image by preserving the source disk layout and
    /// replacing only dirty partitions. Partitions keep their original size; a
    /// replacement larger than the source partition is rejected.
    /// </summary>
    public static class DiskImageWriter
    {
        public static byte[] Build(BaseImageBlob Blob, out string Error)
        {
            Error = null;
            try
            {
                var Source = Blob.BuildPlan?.Root?.Raw;
                if (Source == null)
                {
                    Error = "Disk image writer needs an existing source image";
                    return null;
                }

                if (!DiskImage.TryParse(Source, out var Disk))
                {
                    Error = "Source is not a valid MBR/GPT disk image";
                    return null;
                }

                if (!IsDirty(Blob.BuildPlan.Root))
                    return Source;

                var Result = (byte[])Source.Clone();
                foreach (var Partition in Disk.Partitions)
                {
                    var Child = FindChild(Blob.BuildPlan.Root, Partition.Name);
                    if (Child == null)
                        continue;

                    var Data = GetChildData(Child);
                    if (Data == null)
                        continue;
                    if (Data.Length > Partition.Size)
                    {
                        Error = $"Partition '{Partition.Name}' is too large for its source slot " +
                            $"({Data.Length} > {Partition.Size} bytes)";
                        return null;
                    }

                    Buffer.BlockCopy(Data, 0, Result, (int)Partition.Offset, Data.Length);
                    if (Data.Length < Partition.Size)
                        Array.Clear(Result, (int)(Partition.Offset + Data.Length), (int)(Partition.Size - Data.Length));
                }

                return Result;
            }
            catch (Exception E)
            {
                Error = E.Message;
                return null;
            }
        }

        private static bool IsDirty(ImageBuildNode Node)
        {
            if (Node == null)
                return false;
            if (Node.Dirty)
                return true;
            foreach (var Child in Node.Children)
                if (IsDirty(Child))
                    return true;
            return false;
        }

        private static ImageBuildNode FindChild(ImageBuildNode Node, string Name)
        {
            if (Node == null)
                return null;
            foreach (var Child in Node.Children)
            {
                if (String.Equals(Child.Name, Name, StringComparison.OrdinalIgnoreCase))
                    return Child;
                var Res = FindChild(Child, Name);
                if (Res != null)
                    return Res;
            }
            return null;
        }

        private static byte[] GetChildData(ImageBuildNode Child)
        {
            if (!Child.Dirty && (Child.Raw != null))
                return Child.Raw;

            var Writer = new ImageBuildPlanWriter(new ImageBuildPlan(Child));
            var Result = Writer.Build(out _);
            return (Result != null) ? Result : Child.Raw;
        }
    }
}