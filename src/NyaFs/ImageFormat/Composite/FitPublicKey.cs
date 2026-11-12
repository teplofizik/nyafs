using NyaFs.FlattenedDeviceTree.Types;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Builds the U-Boot control DTB /signature node used by FIT verified boot.
    /// </summary>
    public static class FitPublicKey
    {
        public static string DefaultAlgo(RSA Rsa) => $"sha256,rsa{Rsa.KeySize}";

        public static byte[] BuildControlDtb(RSA Rsa, string KeyName, string Algo = null,
            string Required = "conf")
        {
            var Parameters = Rsa.ExportParameters(false);
            var Bits = Rsa.KeySize;
            var Size = (Bits + 7) / 8;
            var Modulus = PadLeft(Parameters.Modulus, Size);
            var Exponent = PadLeft(Parameters.Exponent, 8);
            var ModulusValue = FromBigEndian(Modulus);
            var R2 = BigInteger.ModPow(new BigInteger(2), new BigInteger(2 * Bits), ModulusValue);
            var R2Bytes = PadLeft(ToBigEndian(R2), Size);
            var N0 = ModInverse32(LowWord(Modulus));

            var Root = new Node("");
            var Signature = new Node("signature");
            var Key = new Node("key-" + SafeName(String.IsNullOrEmpty(KeyName) ? "key" : KeyName));

            if (!String.IsNullOrEmpty(Required))
                Key.AddStringValue("required", Required);
            Key.AddStringValue("algo", String.IsNullOrEmpty(Algo) ? DefaultAlgo(Rsa) : Algo);
            Key.AddRawValue("rsa,r-squared", Cells(R2Bytes));
            Key.AddRawValue("rsa,modulus", Cells(Modulus));
            Key.AddRawValue("rsa,exponent", Cells(Exponent));
            Key.AddUInt32Value("rsa,n0-inverse", N0);
            Key.AddUInt32Value("rsa,num-bits", (uint)Bits);
            Key.AddStringValue("key-name-hint", KeyName ?? "");

            Signature.Nodes.Add(Key);
            Root.Nodes.Add(Signature);
            return new FlattenedDeviceTree.Writer.FDTWriter(new FlattenedDeviceTree.FlattenedDeviceTree { Root = Root }).GetBinary();
        }

        private static byte[] Cells(byte[] Value)
        {
            var Padded = PadLeft(Value, (Value.Length + 3) & ~3);
            return Padded;
        }

        private static byte[] PadLeft(byte[] Value, int Size)
        {
            if (Value.Length == Size)
                return Value;
            var Result = new byte[Size];
            Buffer.BlockCopy(Value, 0, Result, Size - Value.Length, Value.Length);
            return Result;
        }

        private static byte[] ToBigEndian(BigInteger Value)
        {
            var Little = Value.ToByteArray();
            var Result = new byte[Little.Length];
            for (var I = 0; I < Little.Length; I++)
                Result[I] = Little[Little.Length - 1 - I];
            return Result;
        }

        private static BigInteger FromBigEndian(byte[] Value)
        {
            var Little = new byte[Value.Length + 1];
            for (var I = 0; I < Value.Length; I++)
                Little[I] = Value[Value.Length - 1 - I];
            return new BigInteger(Little);
        }

        private static uint LowWord(byte[] Modulus) =>
            (uint)((Modulus[Modulus.Length - 4] << 24) | (Modulus[Modulus.Length - 3] << 16) |
                   (Modulus[Modulus.Length - 2] << 8) | Modulus[Modulus.Length - 1]);

        private static uint ModInverse32(uint Value)
        {
            var Inverse = 1u;
            for (var I = 0; I < 5; I++)
                Inverse *= 2u - Value * Inverse;
            return 0u - Inverse;
        }

        private static string SafeName(string Name)
        {
            var Result = new StringBuilder();
            foreach (var C in Name)
                Result.Append(Char.IsLetterOrDigit(C) || (C == '-') || (C == '_') || (C == '.') ? C : '_');
            return Result.Length == 0 ? "key" : Result.ToString();
        }
    }
}
