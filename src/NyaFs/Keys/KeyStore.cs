using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NyaFs.Keys
{
    public enum KeyKind
    {
        Asymmetric,
        Symmetric
    }

    public enum KeyUsage
    {
        Sign,
        Verify,
        Encrypt,
        Decrypt,
        SignVerify,
        EncryptDecrypt
    }

    /// <summary>
    /// One key profile. Public material is stored as-is; private/symmetric material
    /// is protected by the platform provider before it reaches disk.
    /// </summary>
    public class KeyRecord
    {
        public string Id = "";
        public string Name = "";
        public KeyKind Kind = KeyKind.Asymmetric;
        public KeyUsage Usage = KeyUsage.Sign;
        public string Algorithm = "";
        public string Fingerprint = "";
        public byte[] Certificate = null;
        public byte[] PublicKey = null;
        public byte[] ProtectedPrivate = null;
        public byte[] ProtectedSecret = null;

        public override string ToString() =>
            $"{Id} ({Name}) {Kind}/{Usage} {Algorithm} {Fingerprint}";
    }

    public interface IKeyStore
    {
        string ActiveProfile { get; set; }
        IEnumerable<KeyRecord> List();
        KeyRecord Find(string Id);
        KeyRecord FindByFingerprint(string Fingerprint);
        bool Save(KeyRecord Record, out string Error);
        bool Remove(string Id, out string Error);
        byte[] GetPrivateMaterial(KeyRecord Record);
        byte[] GetSymmetricSecret(KeyRecord Record);
    }

    /// <summary>
    /// User key store. On Windows private material is protected with DPAPI
    /// (CurrentUser); other platforms are intentionally rejected until a
    /// libsecret/0600 provider is implemented.
    /// </summary>
    public class FileKeyStore : IKeyStore
    {
        private readonly string Root;
        public string ActiveProfile { get; set; }

        public FileKeyStore(string Root = null)
        {
            if (String.IsNullOrEmpty(Root))
            {
                var Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NyaFs", "keys");
                else
                    Root = Path.Combine(Home, ".config", "nyafs", "keys");
            }

            this.Root = Root;
        }

        public IEnumerable<KeyRecord> List()
        {
            var Result = new List<KeyRecord>();
            if (!Directory.Exists(Root))
                return Result;

            foreach (var File in Directory.GetFiles(Root, "*.key"))
            {
                try
                {
                    var Record = Read(File);
                    if (Record != null)
                        Result.Add(Record);
                }
                catch
                {
                }
            }
            Result.Sort((A, B) => String.CompareOrdinal(A.Id, B.Id));
            return Result;
        }

        public KeyRecord Find(string Id)
        {
            if (String.IsNullOrEmpty(Id))
                return null;
            foreach (var Record in List())
                if (String.Equals(Record.Id, Id, StringComparison.OrdinalIgnoreCase))
                    return Record;
            return null;
        }

        public KeyRecord FindByFingerprint(string Fingerprint)
        {
            if (String.IsNullOrEmpty(Fingerprint))
                return null;
            foreach (var Record in List())
                if (String.Equals(Record.Fingerprint, Fingerprint, StringComparison.OrdinalIgnoreCase))
                    return Record;
            return null;
        }

        public bool Save(KeyRecord Record, out string Error)
        {
            Error = null;
            try
            {
                Directory.CreateDirectory(Root);
                var Lines = new List<string>
                {
                    "id=" + Record.Id,
                    "name=" + Record.Name,
                    "kind=" + Record.Kind,
                    "usage=" + Record.Usage,
                    "algorithm=" + (Record.Algorithm ?? ""),
                    "fingerprint=" + (Record.Fingerprint ?? ""),
                    "certificate=" + ToBase64(Record.Certificate),
                    "public=" + ToBase64(Record.PublicKey),
                    "private=" + ToBase64(Record.ProtectedPrivate),
                    "secret=" + ToBase64(Record.ProtectedSecret)
                };
                File.WriteAllText(Path.Combine(Root, SafeName(Record.Id) + ".key"), String.Join("\n", Lines), new UTF8Encoding(false));
                return true;
            }
            catch (Exception E)
            {
                Error = E.Message;
                return false;
            }
        }

        public bool Remove(string Id, out string Error)
        {
            Error = null;
            try
            {
                var File = Path.Combine(Root, SafeName(Id) + ".key");
                if (System.IO.File.Exists(File))
                    System.IO.File.Delete(File);
                return true;
            }
            catch (Exception E)
            {
                Error = E.Message;
                return false;
            }
        }

        public byte[] GetPrivateMaterial(KeyRecord Record) =>
            Unprotect(Record.ProtectedPrivate);

        public byte[] GetSymmetricSecret(KeyRecord Record) =>
            Unprotect(Record.ProtectedSecret);

        public static byte[] Protect(byte[] Data)
        {
            if (Data == null)
                return null;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("Secure key storage is not implemented on this platform");

            return ProtectDpapi(Data);
        }

        public static byte[] Unprotect(byte[] Data)
        {
            if (Data == null)
                return null;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("Secure key storage is not implemented on this platform");

            return UnprotectDpapi(Data);
        }

        public static string Fingerprint(byte[] Data)
        {
            if (Data == null)
                return "";
            using (var Sha = SHA256.Create())
                return BitConverter.ToString(Sha.ComputeHash(Data)).Replace("-", "").ToLowerInvariant();
        }

        private KeyRecord Read(string File)
        {
            var Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var Line in System.IO.File.ReadAllLines(File))
            {
                var Index = Line.IndexOf('=');
                if (Index <= 0)
                    continue;
                Fields[Line.Substring(0, Index)] = Line.Substring(Index + 1);
            }

            if (!Fields.TryGetValue("id", out var Id) || String.IsNullOrEmpty(Id))
                return null;

            KeyKind Kind;
            KeyUsage Usage;
            if (!Enum.TryParse(Fields.TryGetValue("kind", out var K) ? K : "Asymmetric", out Kind))
                Kind = KeyKind.Asymmetric;
            if (!Enum.TryParse(Fields.TryGetValue("usage", out var U) ? U : "Sign", out Usage))
                Usage = KeyUsage.Sign;

            return new KeyRecord
            {
                Id = Id,
                Name = Fields.TryGetValue("name", out var N) ? N : Id,
                Kind = Kind,
                Usage = Usage,
                Algorithm = Fields.TryGetValue("algorithm", out var A) ? A : "",
                Fingerprint = Fields.TryGetValue("fingerprint", out var F) ? F : "",
                Certificate = FromBase64(Fields.TryGetValue("certificate", out var C) ? C : ""),
                PublicKey = FromBase64(Fields.TryGetValue("public", out var P) ? P : ""),
                ProtectedPrivate = FromBase64(Fields.TryGetValue("private", out var Pr) ? Pr : ""),
                ProtectedSecret = FromBase64(Fields.TryGetValue("secret", out var S) ? S : "")
            };
        }

        private static string ToBase64(byte[] Data) => (Data == null) ? "" : Convert.ToBase64String(Data);
        private static byte[] FromBase64(string Text) => String.IsNullOrEmpty(Text) ? null : Convert.FromBase64String(Text);

        private static string SafeName(string Name)
        {
            var Result = new StringBuilder();
            foreach (var C in (Name ?? ""))
                Result.Append(Char.IsLetterOrDigit(C) || (C == '-') || (C == '_') || (C == '.') ? C : '_');
            return (Result.Length > 0) ? Result.ToString() : "key";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("Crypt32.dll", SetLastError = true)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("Crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("Kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[] ProtectDpapi(byte[] Data)
        {
            var Input = new DATA_BLOB { cbData = Data.Length, pbData = Marshal.AllocHGlobal(Data.Length) };
            var Output = new DATA_BLOB();
            try
            {
                Marshal.Copy(Data, 0, Input.pbData, Data.Length);
                if (!CryptProtectData(ref Input, "NyaFs key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref Output))
                    throw new CryptographicException("DPAPI protect failed: " + Marshal.GetLastWin32Error());

                var Result = new byte[Output.cbData];
                Marshal.Copy(Output.pbData, Result, 0, Output.cbData);
                return Result;
            }
            finally
            {
                if (Input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(Input.pbData);
                if (Output.pbData != IntPtr.Zero) LocalFree(Output.pbData);
            }
        }

        private static byte[] UnprotectDpapi(byte[] Data)
        {
            var Input = new DATA_BLOB { cbData = Data.Length, pbData = Marshal.AllocHGlobal(Data.Length) };
            var Output = new DATA_BLOB();
            try
            {
                Marshal.Copy(Data, 0, Input.pbData, Data.Length);
                if (!CryptUnprotectData(ref Input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref Output))
                    throw new CryptographicException("DPAPI unprotect failed: " + Marshal.GetLastWin32Error());

                var Result = new byte[Output.cbData];
                Marshal.Copy(Output.pbData, Result, 0, Output.cbData);
                return Result;
            }
            finally
            {
                if (Input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(Input.pbData);
                if (Output.pbData != IntPtr.Zero) LocalFree(Output.pbData);
            }
        }
    }
}
