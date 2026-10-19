using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Packages
{
    /// <summary>
    /// Extensible package metadata stored in the "PACKAGE" control file as
    /// "key: value" lines. Unknown keys are preserved, so the format can grow.
    /// </summary>
    public class PackageMetadata
    {
        private readonly SortedDictionary<string, string> Fields =
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Name { get => Get("name"); set => Set("name", value); }
        public string Version { get => Get("version"); set => Set("version", value); }
        public string Arch { get => Get("arch"); set => Set("arch", value); }
        public string Os { get => Get("os"); set => Set("os", value); }
        public string Description { get => Get("description"); set => Set("description", value); }

        public string Get(string Key) => Fields.TryGetValue(Key, out string V) ? V : "";
        public void Set(string Key, string Value)
        {
            if (string.IsNullOrEmpty(Key))
                return;
            Fields[Key] = Value ?? "";
        }

        public IEnumerable<KeyValuePair<string, string>> All => Fields;

        public byte[] ToControl()
        {
            var Sb = new StringBuilder();
            foreach (var F in Fields)
                Sb.Append(F.Key).Append(": ").Append(F.Value).Append('\n');
            return Encoding.UTF8.GetBytes(Sb.ToString());
        }

        public static PackageMetadata Parse(byte[] Data)
        {
            var Res = new PackageMetadata();
            if (Data == null)
                return Res;

            foreach (var Raw in Encoding.UTF8.GetString(Data).Replace("\r", "").Split('\n'))
            {
                var Line = Raw.Trim();
                if ((Line.Length == 0) || Line.StartsWith("#"))
                    continue;

                int Idx = Line.IndexOf(':');
                if (Idx < 0)
                    Idx = Line.IndexOf('=');
                if (Idx <= 0)
                    continue;

                var Key = Line.Substring(0, Idx).Trim();
                var Value = Line.Substring(Idx + 1).Trim();
                Res.Set(Key, Value);
            }

            return Res;
        }

        public override string ToString()
        {
            var Sb = new StringBuilder();
            Sb.Append("name=").Append(Name);
            if (Version.Length > 0) Sb.Append(" version=").Append(Version);
            if (Arch.Length > 0) Sb.Append(" arch=").Append(Arch);
            if (Os.Length > 0) Sb.Append(" os=").Append(Os);
            return Sb.ToString();
        }
    }
}
