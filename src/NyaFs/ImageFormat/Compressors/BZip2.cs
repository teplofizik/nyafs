using System.IO;

namespace NyaFs.ImageFormat.Compressors
{
    public static class Bzip2
    {
        public static byte[] CompressWithHeader(byte[] Data)
        {
            using (var output = new MemoryStream())
            {
                using (var encoded = new ICSharpCode.SharpZipLib.BZip2.BZip2OutputStream(output))
                {
                    encoded.Write(Data);
                    encoded.Close();

                    return output.ToArray();
                }
            }
        }


        public static byte[] Decompress(byte[] Data)
        {
            using (var inputraw = new MemoryStream(Data))
            {
                using (var input = new ICSharpCode.SharpZipLib.BZip2.BZip2InputStream(inputraw))
                {
                    using (var output = new MemoryStream())
                    {
                        input.CopyTo(output);

                        return output.ToArray();
                    }
                }
            }
        }
    }
}
