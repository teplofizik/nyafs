using System;

namespace NyaFs.Remote
{
    public static class RemoteAuthResolver
    {
        public static RemoteAuth Resolve(string LoginOrProfile, string Password, ICredentialStore Store = null)
        {
            if (String.IsNullOrEmpty(LoginOrProfile))
                throw new ArgumentException("Login or @profile is required", nameof(LoginOrProfile));

            if (LoginOrProfile[0] == '@')
            {
                var Name = LoginOrProfile.Substring(1);
                if (Name.Length == 0)
                    throw new ArgumentException("Auth profile name is required after '@'", nameof(LoginOrProfile));

                return (Store ?? RemoteAuthStore.Default).Get(Name);
            }

            return RemoteAuth.FromPassword(LoginOrProfile, Password == "-" ? "" : (Password ?? ""));
        }
    }
}
