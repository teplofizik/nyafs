using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Reader
{
    public class Reader
    {
        /// <summary>
        /// Читаем в файловую систему из внешнего источника
        /// </summary>
        /// <param name="Dst"></param>
        public virtual void ReadToFs(LinuxFilesystem Dst)
        {

        }

        protected static DateTime ConvertFromUnixTimestamp(long timestamp)
        {
            DateTime origin = new DateTime(1970, 1, 1, 0, 0, 0, 0);
            return origin.AddSeconds(timestamp);
        }

        protected bool DetectAndRead(LinuxFilesystem Dst, byte[] Raw)
        {
            var Plugin = FilesystemDetector.DetectFilesystemPlugin(new ImageData(Raw));
            var Reader = Plugin?.GetFilesystemReader(Raw);
            if (Reader == null)
                return false;

            Reader.ReadToFs(Dst);
            Dst.FilesystemFormat = Plugin.Format;
            return true;
        }
    }
}
