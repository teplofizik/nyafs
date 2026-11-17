using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Dtb.Writer
{
    public class Writer
    {
        public virtual void WriteDevtree(DeviceTree Dtb)
        {

        }

        public virtual bool HasRawStreamData => RawStream != null;

        public virtual byte[] RawStream => null;
    }
}
