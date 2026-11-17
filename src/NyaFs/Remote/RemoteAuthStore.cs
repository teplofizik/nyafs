using System;

namespace NyaFs.Remote
{
    public static class RemoteAuthStore
    {
        private static ICredentialStore Store = new FileCredentialStore();

        public static ICredentialStore Default => Store;

        public static void Use(ICredentialStore CredentialStore)
        {
            Store = CredentialStore ?? throw new ArgumentNullException(nameof(CredentialStore));
        }

        public static void Reset()
        {
            Store = new FileCredentialStore();
        }
    }
}
