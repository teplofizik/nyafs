using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Composite
{
    public class AndroidImageWriter
    {
        private readonly string Path;

        public AndroidImageWriter(string Path)
        {
            this.Path = Path;
        }

        public bool Write(BaseImageBlob Blob)
        {

            return false;
        }
    }
}
