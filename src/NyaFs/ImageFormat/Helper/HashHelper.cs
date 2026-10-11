using CrcSharp;
using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Helper
{
    public static class HashHelper
    {
        /// <summary>
        /// Calc standart CRC32 (0x04c11db7)
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static uint Crc32(byte[] data)
        {
            var crc32 = new Crc(new CrcParameters(32, 0x04c11db7, 0xffffffff, 0xffffffff, true, true));

            return Convert.ToUInt32(crc32.CalculateAsNumeric(data));
        }
    }
}
