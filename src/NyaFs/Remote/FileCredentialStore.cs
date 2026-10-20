using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NyaFs.Remote
{
    public sealed class FileCredentialStore : ICredentialStore
    {
        public string FilePath { get; }

        public string Location => FilePath;

        public FileCredentialStore(string FilePath = null)
        {
            this.FilePath = FilePath ?? DefaultPath();
        }

        public static string DefaultPath()
        {
            var Override = Environment.GetEnvironmentVariable("NYAFS_AUTH_FILE");
            if (!String.IsNullOrEmpty(Override))
                return Override;

            if (Path.DirectorySeparatorChar == '\\')
            {
                var Base = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (String.IsNullOrEmpty(Base))
                    Base = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                return Path.Combine(Base, "NyaFs", "auth.txt");
            }

            var Xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!String.IsNullOrEmpty(Xdg))
                return Path.Combine(Xdg, "nyafs", "auth.txt");

            var Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(Home, ".config", "nyafs", "auth.txt");
        }

        public RemoteAuth Get(string Name)
        {
            ValidateName(Name);

            var Profiles = Load();
            if (!Profiles.TryGetValue(Name, out RemoteAuth Auth))
                throw new InvalidOperationException($"Auth profile '{Name}' was not found in {FilePath}");

            return Auth;
        }

        public void Set(string Name, RemoteAuth Auth)
        {
            ValidateName(Name);

            if (Auth == null)
                throw new ArgumentNullException(nameof(Auth));

            if (Auth.IsEmpty)
                throw new InvalidOperationException("Auth profile must contain a password or a private key");

            var Profiles = Load();
            Profiles[Name] = Auth;
            Save(Profiles);
        }

        public bool Remove(string Name)
        {
            ValidateName(Name);

            var Profiles = Load();
            if (!Profiles.Remove(Name))
                return false;

            Save(Profiles);
            return true;
        }

        public RemoteAuthProfile[] List()
        {
            var Profiles = Load();
            var Names = new List<string>(Profiles.Keys);
            Names.Sort(StringComparer.Ordinal);

            var Result = new RemoteAuthProfile[Names.Count];
            for (int i = 0; i < Names.Count; i++)
            {
                Result[i] = new RemoteAuthProfile
                {
                    Name = Names[i],
                    Auth = Profiles[Names[i]]
                };
            }

            return Result;
        }

        private Dictionary<string, RemoteAuth> Load()
        {
            var Result = new Dictionary<string, RemoteAuth>(StringComparer.Ordinal);
            if (!File.Exists(FilePath))
                return Result;

            RemoteAuth Current = null;
            string CurrentName = null;
            string[] Lines = File.ReadAllLines(FilePath, Encoding.UTF8);

            for (int LineNo = 0; LineNo < Lines.Length; LineNo++)
            {
                var Line = Lines[LineNo].Trim();
                if ((Line.Length == 0) || Line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (Line.StartsWith("[", StringComparison.Ordinal) && Line.EndsWith("]", StringComparison.Ordinal))
                {
                    if (CurrentName != null)
                        Result[CurrentName] = Current;

                    CurrentName = Line.Substring(1, Line.Length - 2).Trim();
                    ValidateName(CurrentName);
                    Current = new RemoteAuth();
                    continue;
                }

                if (Current == null)
                    throw new InvalidDataException($"Invalid auth file {FilePath}:{LineNo + 1}: value outside a profile");

                int Eq = Line.IndexOf('=');
                if (Eq <= 0)
                    throw new InvalidDataException($"Invalid auth file {FilePath}:{LineNo + 1}: expected key=value");

                var Key = Line.Substring(0, Eq).Trim().ToLowerInvariant();
                var Value = Decode(Line.Substring(Eq + 1).Trim(), FilePath, LineNo + 1);

                switch (Key)
                {
                    case "login": Current.Login = Value; break;
                    case "password": Current.Password = Value; break;
                    case "key": Current.PrivateKeyPath = Value; break;
                    case "passphrase": Current.Passphrase = Value; break;
                    default:
                        throw new InvalidDataException($"Invalid auth file {FilePath}:{LineNo + 1}: unknown field '{Key}'");
                }
            }

            if (CurrentName != null)
                Result[CurrentName] = Current;

            return Result;
        }

        private void Save(Dictionary<string, RemoteAuth> Profiles)
        {
            var Dir = Path.GetDirectoryName(FilePath);
            if (!String.IsNullOrEmpty(Dir))
                Directory.CreateDirectory(Dir);

            var Names = new List<string>(Profiles.Keys);
            Names.Sort(StringComparer.Ordinal);

            var Text = new StringBuilder();
            Text.AppendLine("# NyaFs remote auth profiles");
            Text.AppendLine("# Values are base64(UTF-8). Keep this file private.");

            foreach (var Name in Names)
            {
                var Auth = Profiles[Name];
                Text.AppendLine();
                Text.AppendLine("[" + Name + "]");

                if (!String.IsNullOrEmpty(Auth.Login))
                    Text.AppendLine("login=" + Encode(Auth.Login));

                if (!String.IsNullOrEmpty(Auth.Password))
                    Text.AppendLine("password=" + Encode(Auth.Password));

                if (!String.IsNullOrEmpty(Auth.PrivateKeyPath))
                    Text.AppendLine("key=" + Encode(Auth.PrivateKeyPath));

                if (!String.IsNullOrEmpty(Auth.Passphrase))
                    Text.AppendLine("passphrase=" + Encode(Auth.Passphrase));
            }

            File.WriteAllText(FilePath, Text.ToString(), new UTF8Encoding(false));
            Protect(FilePath);
        }

        private static void Protect(string Path)
        {
#if NET6_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
        }

        private static string Encode(string Value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Value ?? ""));

        private static string Decode(string Value, string FilePath, int Line)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(Value));
            }
            catch (FormatException)
            {
                throw new InvalidDataException($"Invalid auth file {FilePath}:{Line}: value is not valid base64");
            }
        }

        private static void ValidateName(string Name)
        {
            if (String.IsNullOrEmpty(Name))
                throw new ArgumentException("Auth profile name must not be empty", nameof(Name));

            if (Name.Length > 64)
                throw new ArgumentException("Auth profile name is too long", nameof(Name));

            foreach (var C in Name)
            {
                if (!Char.IsLetterOrDigit(C) && (C != '.') && (C != '_') && (C != '-'))
                    throw new ArgumentException(
                        $"Invalid auth profile name '{Name}': use letters, digits, '.', '_' or '-'", nameof(Name));
            }
        }
    }
}
