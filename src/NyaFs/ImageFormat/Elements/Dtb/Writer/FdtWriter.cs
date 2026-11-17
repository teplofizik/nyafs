using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Dtb.Writer
{
    /// <summary>
    /// Plain flattened device tree (DTB) writer.
    /// </summary>
    public class FdtWriter : Writer
    {
        string Filename;
        byte[] Data = null;

        public FdtWriter()
        {

        }

        public FdtWriter(string Filename)
        {
            this.Filename = Filename;
        }

        public override void WriteDevtree(DeviceTree Dtb)
        {
            var Binary = new FlattenedDeviceTree.Writer.FDTWriter(Dtb.DevTree).GetBinary();

            if (Filename != null)
            {
                Data = null;
                System.IO.File.WriteAllBytes(Filename, Binary);
            }
            else
                Data = Binary;
        }

        public override byte[] RawStream => Data;
    }
}
