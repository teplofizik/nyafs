namespace NyaFs.Remote
{
    public sealed class RemoteAuthProfile
    {
        public string Name { get; set; }

        public RemoteAuth Auth { get; set; }
    }

    public interface ICredentialStore
    {
        string Location { get; }

        RemoteAuth Get(string Name);

        void Set(string Name, RemoteAuth Auth);

        bool Remove(string Name);

        RemoteAuthProfile[] List();
    }
}
