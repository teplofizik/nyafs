using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Items;
using NyaFs.ImageFormat.Elements.Fs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NyaFs.Packages
{
    /// <summary>
    /// Package installation database stored as metadata files under
    /// /var/lib/nya/packages. It is intentionally simple: one control-style
    /// file per installed package, no package payload duplication.
    /// </summary>
    public class PackageDatabase
    {
        public const string DatabaseDir = "/var/lib/nya/packages";

        private readonly SortedDictionary<string, PackageMetadata> Packages =
            new SortedDictionary<string, PackageMetadata>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<PackageMetadata> Installed => Packages.Values;

        public PackageMetadata Find(string Name) =>
            Packages.TryGetValue(Name ?? "", out var P) ? P : null;

        public static PackageDatabase Load(LinuxFilesystem Fs)
        {
            var Result = new PackageDatabase();
            try
            {
                var Dir = Fs.GetDirectory(DatabaseDir);
                if (Dir == null)
                    return Result;

                foreach (var Item in Dir.Items)
                {
                    var File = Item as NyaFs.Filesystem.Universal.Items.File;
                    if (File == null)
                        continue;

                    var Meta = PackageMetadata.Parse(File.Content);
                    if (!String.IsNullOrEmpty(Meta.Name))
                        Result.Packages[Meta.Name] = Meta;
                }
            }
            catch (ArgumentException)
            {
            }
            return Result;
        }

        public void Set(PackageMetadata Meta)
        {
            if ((Meta == null) || String.IsNullOrEmpty(Meta.Name))
                return;
            Packages[Meta.Name] = Meta;
        }

        public bool Save(LinuxFilesystem Fs, out string Error)
        {
            Error = null;
            try
            {
                var Dir = EnsureDirectory(Fs, DatabaseDir);
                if (Dir == null)
                {
                    Error = "Cannot create " + DatabaseDir;
                    return false;
                }

                var Existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var Meta in Packages.Values)
                {
                    var Name = SafeName(Meta.Name) + ".pkg";
                    Existing.Add(Name);
                    var File = Dir.Items.Find(I => String.Equals(I.ShortFilename, Name, StringComparison.OrdinalIgnoreCase)) as NyaFs.Filesystem.Universal.Items.File;
                    if (File == null)
                    {
                        File = new NyaFs.Filesystem.Universal.Items.File(DatabaseDir.TrimEnd('/') + "/" + Name, 0, 0, 0x1A4, new byte[0]);
                        Dir.Items.Add(File);
                    }
                    File.Content = Meta.ToControl();
                    File.Modified = DateTime.Now;
                }

                Dir.Items.RemoveAll(I => (I is NyaFs.Filesystem.Universal.Items.File) &&
                    I.ShortFilename.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) &&
                    !Existing.Contains(I.ShortFilename));
                return true;
            }
            catch (Exception E)
            {
                Error = E.Message;
                return false;
            }
        }

        public bool CheckInstall(PackageMetadata Meta, out string Error)
        {
            Error = null;
            if (Packages.ContainsKey(Meta.Name))
            {
                Error = "Package '" + Meta.Name + "' is already installed";
                return false;
            }

            foreach (var Spec in Meta.GetList("conflicts"))
            {
                var Conflict = ParseSpec(Spec);
                if ((Conflict != null) && Satisfies(Conflict))
                {
                    Error = "Package '" + Meta.Name + "' conflicts with installed '" + Conflict.Name + "'";
                    return false;
                }
            }

            foreach (var Spec in Meta.GetList("depends"))
            {
                var Dep = ParseSpec(Spec);
                if (Dep == null)
                    continue;
                if (!Satisfies(Dep))
                {
                    Error = "Missing dependency '" + Spec + "' for package '" + Meta.Name + "'";
                    return false;
                }
            }

            return true;
        }

        private bool Satisfies(DependencySpec Spec)
        {
            foreach (var Package in Packages.Values)
            {
                if (String.Equals(Package.Name, Spec.Name, StringComparison.OrdinalIgnoreCase) &&
                    VersionMatches(Package.Version, Spec.Op, Spec.Version))
                    return true;

                foreach (var ProvideValue in Package.GetList("provides"))
                {
                    var Provide = ParseSpec(ProvideValue);
                    if ((Provide != null) && String.Equals(Provide.Name, Spec.Name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }
        public static bool Resolve(IEnumerable<NyaPackage> Candidates, string RootName,
            out List<NyaPackage> Order, out string Error)
        {
            var Failure = (string)null;
            Order = new List<NyaPackage>();
            var ResultOrder = new List<NyaPackage>();
            var All = new Dictionary<string, NyaPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var P in Candidates)
            {
                if (!String.IsNullOrEmpty(P.Metadata.Name))
                    All[P.Metadata.Name] = P;
            }

            if (!All.TryGetValue(RootName ?? "", out var Root))
            {
                Error = "Root package '" + RootName + "' not found";
                return false;
            }

            var Visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var Done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool Visit(NyaPackage P)
            {
                var Name = P.Metadata.Name;
                if (Done.Contains(Name))
                    return true;
                if (!Visiting.Add(Name))
                {
                    Failure = "Circular dependency at '" + Name + "'";
                    return false;
                }

                foreach (var Spec in P.Metadata.GetList("depends"))
                {
                    var Dep = ParseSpec(Spec);
                    if ((Dep == null) || !All.TryGetValue(Dep.Name, out var Child))
                    {
                        Failure = "Missing dependency '" + Spec + "' for '" + Name + "'";
                        return false;
                    }
                    if (!VersionMatches(Child.Metadata.Version, Dep.Op, Dep.Version))
                    {
                        Failure = "Dependency '" + Spec + "' has version " + Child.Metadata.Version;
                        return false;
                    }
                    if (!Visit(Child))
                        return false;
                }

                foreach (var Spec in P.Metadata.GetList("conflicts"))
                {
                    var Conflict = ParseSpec(Spec);
                    if ((Conflict != null) && All.TryGetValue(Conflict.Name, out var Other))
                    {
                        Failure = "Package '" + Name + "' conflicts with '" + Conflict.Name + "'";
                        return false;
                    }
                }

                Visiting.Remove(Name);
                Done.Add(Name);
                ResultOrder.Add(P);
                return true;
            }

            var Result = Visit(Root);
            Error = Failure;
            Order = ResultOrder;
            return Result;
        }

        private class DependencySpec
        {
            public string Name;
            public string Op;
            public string Version;
        }

        private static DependencySpec ParseSpec(string Spec)
        {
            if (String.IsNullOrWhiteSpace(Spec))
                return null;

            var Text = Spec.Trim();
            var Ops = new[] { ">=", "<=", "=", ">", "<" };
            foreach (var Op in Ops)
            {
                var Index = Text.IndexOf(Op, StringComparison.Ordinal);
                if (Index <= 0)
                    continue;

                return new DependencySpec
                {
                    Name = Text.Substring(0, Index).Trim(),
                    Op = Op,
                    Version = Text.Substring(Index + Op.Length).Trim()
                };
            }

            return new DependencySpec { Name = Text, Op = "", Version = "" };
        }

        private static bool VersionMatches(string Have, string Op, string Want)
        {
            if (String.IsNullOrEmpty(Op))
                return true;
            var Cmp = CompareVersions(Have, Want);
            switch (Op)
            {
                case "=": return Cmp == 0;
                case ">": return Cmp > 0;
                case ">=": return Cmp >= 0;
                case "<": return Cmp < 0;
                case "<=": return Cmp <= 0;
                default: return true;
            }
        }

        private static int CompareVersions(string A, string B)
        {
            var Left = (A ?? "").Split('.', '-', '+');
            var Right = (B ?? "").Split('.', '-', '+');
            var Count = Math.Max(Left.Length, Right.Length);
            for (var I = 0; I < Count; I++)
            {
                var L = (I < Left.Length) ? Left[I] : "0";
                var R = (I < Right.Length) ? Right[I] : "0";
                long Li;
                long Ri;
                if (Int64.TryParse(L, NumberStyles.None, CultureInfo.InvariantCulture, out Li) &&
                    Int64.TryParse(R, NumberStyles.None, CultureInfo.InvariantCulture, out Ri))
                {
                    if (Li != Ri) return Li.CompareTo(Ri);
                }
                else
                {
                    var Cmp = String.CompareOrdinal(L, R);
                    if (Cmp != 0) return Cmp;
                }
            }
            return 0;
        }

        private static Dir EnsureDirectory(LinuxFilesystem Fs, string Path)
        {
            var Current = Fs.Fs.Root;
            var Parts = Path.Trim('/').Split('/');
            var Acc = "";
            foreach (var Part in Parts)
            {
                Acc = (Acc.Length == 0) ? Part : Acc + "/" + Part;
                var Next = Current.Items.Find(I => (I is Dir) && String.Equals(I.ShortFilename, Part, StringComparison.Ordinal)) as Dir;
                if (Next == null)
                {
                    Next = new Dir(Acc, 0, 0, 0x1ED);
                    Current.Items.Add(Next);
                }
                Current = Next;
            }
            return Current;
        }

        private static string SafeName(string Name)
        {
            var Result = new StringBuilder();
            foreach (var C in (Name ?? ""))
                Result.Append(System.Char.IsLetterOrDigit(C) || (C == '-') || (C == '_') || (C == '.') ? C : '_');
            return (Result.Length > 0) ? Result.ToString() : "package";
        }
    }
}
