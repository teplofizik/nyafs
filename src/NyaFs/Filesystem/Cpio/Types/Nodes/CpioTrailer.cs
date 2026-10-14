using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Cpio.Types.Nodes
{
    public class CpioTrailer : CpioNode
    {
        public CpioTrailer() : base("TRAILER!!!", new byte[] { }, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0x0000U, 0, 0)
        {

        }
    }
}
