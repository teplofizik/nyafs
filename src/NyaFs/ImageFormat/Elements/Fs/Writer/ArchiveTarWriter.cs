using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Writer
{
    public class ArchiveTarWriter : Writer
    {
        string Filename;
        byte[] PackedData = null;
        Types.CompressionType Compression;

        public ArchiveTarWriter(Types.CompressionType Compression)
        {
            this.Compression = Compression;
        }

        public ArchiveTarWriter(string Filename, Types.CompressionType Compression)
        {
            this.Filename = Filename;
            this.Compression = Compression;
        }

        public override void WriteFs(LinuxFilesystem Fs)
        {
            var TarWriter = new TarFsWriter();
            TarWriter.WriteFs(Fs);

            var Data = Helper.FitHelper.GetCompressedData(TarWriter.RawStream, Compression);

            if (Filename != null)
            {
                PackedData = null;
                System.IO.File.WriteAllBytes(Filename, Data);
            }
            else
                PackedData = Data;
        }

        public override byte[] RawStream => PackedData;
    }
}
