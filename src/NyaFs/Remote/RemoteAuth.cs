using System;

namespace NyaFs.Remote
{
    public sealed class RemoteAuth
    {
        public string Login { get; set; }

        public string Password { get; set; }

        public string PrivateKeyPath { get; set; }

        public string Passphrase { get; set; }

        public bool HasPassword => !String.IsNullOrEmpty(Password);

        public bool HasPrivateKey => !String.IsNullOrEmpty(PrivateKeyPath);

        public bool IsEmpty => !HasPassword && !HasPrivateKey;

        public string Kind
        {
            get
            {
                if (HasPrivateKey && HasPassword) return "key+password";
                if (HasPrivateKey) return "key";
                if (HasPassword) return "password";
                return "none";
            }
        }

        public static RemoteAuth FromPassword(string Login, string Password)
        {
            return new RemoteAuth
            {
                Login = Login,
                Password = Password
            };
        }

        public static RemoteAuth FromPrivateKey(string Login, string PrivateKeyPath, string Passphrase)
        {
            return new RemoteAuth
            {
                Login = Login,
                PrivateKeyPath = PrivateKeyPath,
                Passphrase = Passphrase
            };
        }
    }
}
