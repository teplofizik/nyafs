using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Importer
{
    internal class ImageImporter
    {
        public ImageImporter() { }

        public bool Import(ImageProcessor P, byte[] Raw)
        {
            return ProcessPackedData(P, Raw);
        }

        private bool ProcessUnpackedData(ImageProcessor P, byte[] Raw)
        {
            var Data = new ImageFormat.ImageData(Raw);

            var Imgs = P.Plugins.GetImages();
            foreach (var Img in Imgs)
            {
                if (Img.DetectImage(Data))
                {
                    var FsReader = Img.GetFilesystemReader(Raw);
                    if (FsReader != null)
                    {
                        var Dest = P.GetFs(true);
                        FsReader.ReadToFs(Dest);
                    }

                    var KernelReader = Img.GetKernelReader(Raw);
                    if (KernelReader != null)
                    {
                        var Dest = P.GetKernel(true);
                        KernelReader.ReadToKernel(Dest);
                    }

                    var DtbReader = Img.GetDtbReader(Raw);
                    if (DtbReader != null)
                    {
                        var Dest = P.GetDevTree(true);
                        DtbReader.ReadToDevTree(Dest);
                    }
                    return true;
                }
            }

            var Kernels = P.Plugins.GetKernels();
            foreach (var Kernel in Kernels)
            {
                if (Kernel.DetectKernel(Data))
                {
                    var Reader = Kernel.GetKernelReader(Raw);
                    if (Reader != null)
                    {
                        var Dest = P.GetKernel(true);
                        Reader.ReadToKernel(Dest);
                        return true;
                    }
                }
            }

            var Fss = P.Plugins.GetFilesystems();
            foreach (var Fs in Fss)
            {
                if(Fs.DetectFilesystem(Data))
                {
                    var Reader = Fs.GetFilesystemReader(Raw);

                    var Dest = P.GetFs();
                    Reader.ReadToFs(Dest);
                    return true;
                }
            }


            return false;
        }

        private bool ProcessPackedData(ImageProcessor P, byte[] Raw)
        {
            var Compressors = P.Plugins.GetCompressors();

            foreach(var Compressor in Compressors)
            {
                if(Compressor.DetectCompressor(new ImageFormat.ImageData(Raw)))
                {
                    // Oh, it's compressed data!..

                    var Unpacked = Compressor.Decompress(Raw);

                    return ProcessUnpackedData(P, Unpacked);
                }
            }

            // It's not a known compressed data
            return ProcessUnpackedData(P, Raw);
        }
    }
}
