using NyaFs.Filesystem.Universal.Items;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NyaFs.Filesystem.Universal.Identity
{
    /// <summary>
    /// A user entry from /etc/passwd. The numeric ids are canonical; the name is
    /// only a lookup aid stored in the filesystem itself.
    /// </summary>
    public class UserRecord
    {
        public string Name = "";
        public string Password = "x";
        public uint Uid;
        public uint Gid;
        public string Gecos = "";
        public string Home = "/";
        public string Shell = "/bin/sh";

        internal bool Modified;

        public string ToLine() => String.Join(":", Name, Password ?? "", Uid.ToString(CultureInfo.InvariantCulture),
            Gid.ToString(CultureInfo.InvariantCulture), Gecos ?? "", Home ?? "", Shell ?? "");
    }

    /// <summary>A group entry from /etc/group.</summary>
    public class GroupRecord
    {
        public string Name = "";
        public string Password = "x";
        public uint Gid;
        public readonly List<string> Members = new List<string>();

        internal bool Modified;

        public string ToLine() => String.Join(":", Name, Password ?? "", Gid.ToString(CultureInfo.InvariantCulture),
            String.Join(",", Members));
    }

    /// <summary>
    /// Logical user/group view of one filesystem. Linux filesystems store only numeric
    /// uid/gid on inodes; names live in /etc/passwd and /etc/group inside the image.
    /// This database is a cache/editor for those files, not filesystem metadata.
    /// </summary>
    public class FsIdentityDatabase
    {
        public const string PasswdPath = "/etc/passwd";
        public const string GroupPath = "/etc/group";

        private class LineEntry
        {
            public string Raw;
            public UserRecord User;
            public GroupRecord Group;
        }

        private readonly Filesystem Fs;
        private readonly List<LineEntry> PasswdLines = new List<LineEntry>();
        private readonly List<LineEntry> GroupLines = new List<LineEntry>();

        public readonly List<UserRecord> Users = new List<UserRecord>();
        public readonly List<GroupRecord> Groups = new List<GroupRecord>();
        public readonly List<string> Warnings = new List<string>();

        public bool Loaded { get; private set; }
        public bool PasswdAvailable { get; private set; }
        public bool GroupAvailable { get; private set; }
        private bool PasswdDirty;
        private bool GroupDirty;

        public FsIdentityDatabase(Filesystem Fs)
        {
            this.Fs = Fs;
        }

        public bool Load()
        {
            Users.Clear();
            Groups.Clear();
            Warnings.Clear();
            PasswdLines.Clear();
            GroupLines.Clear();
            PasswdAvailable = false;
            GroupAvailable = false;
            PasswdDirty = false;
            GroupDirty = false;
            Loaded = true;

            string Error;
            string Text;
            if (ReadText(PasswdPath, out Text, out Error))
            {
                PasswdAvailable = true;
                ParsePasswd(Text);
            }
            else if (!String.IsNullOrEmpty(Error))
                Warnings.Add(Error);

            Error = null;
            Text = null;
            if (ReadText(GroupPath, out Text, out Error))
            {
                GroupAvailable = true;
                ParseGroup(Text);
            }
            else if (!String.IsNullOrEmpty(Error))
                Warnings.Add(Error);

            return PasswdAvailable || GroupAvailable;
        }

        public void Clear()
        {
            Users.Clear();
            Groups.Clear();
            Warnings.Clear();
            PasswdLines.Clear();
            GroupLines.Clear();
            Loaded = false;
            PasswdAvailable = false;
            GroupAvailable = false;
            PasswdDirty = false;
            GroupDirty = false;
        }

        public UserRecord FindUser(string Name)
        {
            EnsureLoaded();
            return Users.FirstOrDefault(U => U.Name == Name);
        }

        public UserRecord FindUser(uint Uid)
        {
            EnsureLoaded();
            return Users.FirstOrDefault(U => U.Uid == Uid);
        }

        public GroupRecord FindGroup(string Name)
        {
            EnsureLoaded();
            return Groups.FirstOrDefault(G => G.Name == Name);
        }

        public GroupRecord FindGroup(uint Gid)
        {
            EnsureLoaded();
            return Groups.FirstOrDefault(G => G.Gid == Gid);
        }

        public string GetUserName(uint Uid) => FindUser(Uid)?.Name;

        public string GetGroupName(uint Gid) => FindGroup(Gid)?.Name;

        public bool TryGetUserId(string Value, out uint Id)
        {
            if (UInt32.TryParse(Value, NumberStyles.None, CultureInfo.InvariantCulture, out Id))
                return true;

            var User = FindUser(Value);
            if (User != null)
            {
                Id = User.Uid;
                return true;
            }

            return false;
        }

        public bool TryGetGroupId(string Value, out uint Id)
        {
            if (UInt32.TryParse(Value, NumberStyles.None, CultureInfo.InvariantCulture, out Id))
                return true;

            var Group = FindGroup(Value);
            if (Group != null)
            {
                Id = Group.Gid;
                return true;
            }

            return false;
        }

        public bool AddUser(string Name, uint Uid, uint Gid, string Gecos, string Home, string Shell, out string Error)
        {
            EnsureLoaded();
            Error = null;

            if (!IsValidName(Name))
            {
                Error = $"Invalid user name '{Name}'";
                return false;
            }
            if (FindUser(Name) != null)
            {
                Error = $"User '{Name}' already exists";
                return false;
            }
            if (FindUser(Uid) != null)
            {
                Error = $"UID {Uid} already exists";
                return false;
            }

            var User = new UserRecord
            {
                Name = Name,
                Uid = Uid,
                Gid = Gid,
                Gecos = Gecos ?? "",
                Home = String.IsNullOrEmpty(Home) ? "/" : Home,
                Shell = String.IsNullOrEmpty(Shell) ? "/bin/sh" : Shell,
                Modified = true
            };

            InsertBeforeTrailingBlank(PasswdLines, new LineEntry { User = User });
            Users.Add(User);
            PasswdDirty = true;
            return true;
        }

        public bool UpdateUser(string Name, uint? Uid, uint? Gid, string Home, string Shell, out string Error)
        {
            EnsureLoaded();
            Error = null;

            var User = FindUser(Name);
            if (User == null)
            {
                Error = "User '" + Name + "' not found";
                return false;
            }

            if (Uid.HasValue && (Uid.Value != User.Uid) && (FindUser(Uid.Value) != null))
            {
                Error = "UID " + Uid.Value + " already exists";
                return false;
            }

            if (Uid.HasValue)
                User.Uid = Uid.Value;
            if (Gid.HasValue)
                User.Gid = Gid.Value;
            if (Home != null)
                User.Home = Home;
            if (Shell != null)
                User.Shell = Shell;

            User.Modified = true;
            PasswdDirty = true;
            return true;
        }

        public bool RemoveUser(string Name, out string Error)
        {
            EnsureLoaded();
            Error = null;

            var User = FindUser(Name);
            if (User == null)
            {
                Error = $"User '{Name}' not found";
                return false;
            }

            PasswdLines.RemoveAll(L => ReferenceEquals(L.User, User));
            Users.Remove(User);
            foreach (var Group in Groups)
            {
                if (Group.Members.Remove(Name))
                {
                    Group.Modified = true;
                    GroupDirty = true;
                }
            }
            PasswdDirty = true;
            return true;
        }

        public bool AddGroup(string Name, uint Gid, out string Error)
        {
            EnsureLoaded();
            Error = null;

            if (!IsValidName(Name))
            {
                Error = $"Invalid group name '{Name}'";
                return false;
            }
            if (FindGroup(Name) != null)
            {
                Error = $"Group '{Name}' already exists";
                return false;
            }
            if (FindGroup(Gid) != null)
            {
                Error = $"GID {Gid} already exists";
                return false;
            }

            var Group = new GroupRecord { Name = Name, Gid = Gid, Modified = true };
            InsertBeforeTrailingBlank(GroupLines, new LineEntry { Group = Group });
            Groups.Add(Group);
            GroupDirty = true;
            return true;
        }

        public bool RemoveGroup(string Name, out string Error)
        {
            EnsureLoaded();
            Error = null;

            var Group = FindGroup(Name);
            if (Group == null)
            {
                Error = $"Group '{Name}' not found";
                return false;
            }

            GroupLines.RemoveAll(L => ReferenceEquals(L.Group, Group));
            Groups.Remove(Group);
            GroupDirty = true;
            return true;
        }

        public bool AddUserToGroup(string UserName, string GroupName, out string Error)
        {
            EnsureLoaded();
            Error = null;

            if (FindUser(UserName) == null)
            {
                Error = $"User '{UserName}' not found";
                return false;
            }
            var Group = FindGroup(GroupName);
            if (Group == null)
            {
                Error = $"Group '{GroupName}' not found";
                return false;
            }
            if (!Group.Members.Contains(UserName))
            {
                Group.Members.Add(UserName);
                Group.Modified = true;
                GroupDirty = true;
            }
            return true;
        }

        public bool RemoveUserFromGroup(string UserName, string GroupName, out string Error)
        {
            EnsureLoaded();
            Error = null;

            var Group = FindGroup(GroupName);
            if (Group == null)
            {
                Error = $"Group '{GroupName}' not found";
                return false;
            }
            if (Group.Members.Remove(UserName))
            {
                Group.Modified = true;
                GroupDirty = true;
            }
            return true;
        }

        public bool Save(out string Error)
        {
            EnsureLoaded();
            Error = null;

            if (PasswdDirty && !WriteText(PasswdPath, Serialize(PasswdLines), out Error))
                return false;
            if (GroupDirty && !WriteText(GroupPath, Serialize(GroupLines), out Error))
                return false;

            PasswdDirty = false;
            GroupDirty = false;
            return true;
        }

        public static bool IsValidName(string Name) =>
            !String.IsNullOrEmpty(Name) && (Name.IndexOfAny(new[] { ':', '\n', '\r' }) < 0);

        private void EnsureLoaded()
        {
            if (!Loaded)
                Load();
        }

        private void ParsePasswd(string Text)
        {
            foreach (var Line in SplitText(Text))
            {
                var Entry = new LineEntry { Raw = Line };
                PasswdLines.Add(Entry);

                var Parts = Line.Split(':');
                if (Parts.Length != 7)
                    continue;

                uint Uid;
                uint Gid;
                if (!UInt32.TryParse(Parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out Uid) ||
                    !UInt32.TryParse(Parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out Gid))
                {
                    Warnings.Add($"Invalid uid/gid in {PasswdPath}: {Line}");
                    continue;
                }

                if (Users.Any(U => U.Name == Parts[0] || U.Uid == Uid))
                    Warnings.Add($"Duplicate user name/uid in {PasswdPath}: {Line}");

                var User = new UserRecord
                {
                    Name = Parts[0],
                    Password = Parts[1],
                    Uid = Uid,
                    Gid = Gid,
                    Gecos = Parts[4],
                    Home = Parts[5],
                    Shell = Parts[6]
                };
                Entry.User = User;
                Users.Add(User);
            }
        }

        private void ParseGroup(string Text)
        {
            foreach (var Line in SplitText(Text))
            {
                var Entry = new LineEntry { Raw = Line };
                GroupLines.Add(Entry);

                var Parts = Line.Split(':');
                if (Parts.Length != 4)
                    continue;

                uint Gid;
                if (!UInt32.TryParse(Parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out Gid))
                {
                    Warnings.Add($"Invalid gid in {GroupPath}: {Line}");
                    continue;
                }

                if (Groups.Any(G => G.Name == Parts[0] || G.Gid == Gid))
                    Warnings.Add($"Duplicate group name/gid in {GroupPath}: {Line}");

                var Group = new GroupRecord
                {
                    Name = Parts[0],
                    Password = Parts[1],
                    Gid = Gid
                };
                if (!String.IsNullOrEmpty(Parts[3]))
                {
                    foreach (var Member in Parts[3].Split(','))
                        if (Member.Length > 0)
                            Group.Members.Add(Member);
                }

                Entry.Group = Group;
                Groups.Add(Group);
            }
        }

        private static List<string> SplitText(string Text) =>
            Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        private static string Serialize(List<LineEntry> Lines)
        {
            var Result = new List<string>();
            foreach (var Line in Lines)
            {
                if (Line.User != null)
                    Result.Add(Line.User.Modified ? Line.User.ToLine() : (Line.Raw ?? Line.User.ToLine()));
                else if (Line.Group != null)
                    Result.Add(Line.Group.Modified ? Line.Group.ToLine() : (Line.Raw ?? Line.Group.ToLine()));
                else
                    Result.Add(Line.Raw ?? "");
            }
            return String.Join("\n", Result);
        }

        private static void InsertBeforeTrailingBlank(List<LineEntry> Lines, LineEntry Entry)
        {
            var Last = (Lines.Count > 0) ? Lines[Lines.Count - 1] : null;
            if ((Last != null) && (Last.User == null) && (Last.Group == null) && String.IsNullOrEmpty(Last.Raw))
                Lines.Insert(Lines.Count - 1, Entry);
            else
                Lines.Add(Entry);
        }

        private bool ReadText(string Path, out string Text, out string Error)
        {
            Text = null;
            Error = null;

            FilesystemItem Item;
            try
            {
                Item = Fs.GetElement(Path);
            }
            catch (ArgumentException)
            {
                return false;
            }

            var File = Item as File;
            if (File == null)
            {
                Error = $"{Path} is not a regular file";
                return false;
            }

            Text = Encoding.UTF8.GetString(File.Content ?? new byte[0]);
            return true;
        }

        private bool WriteText(string Path, string Text, out string Error)
        {
            Error = null;

            FilesystemItem Item;
            try
            {
                Item = Fs.GetElement(Path);
            }
            catch (ArgumentException)
            {
                Item = null;
            }

            if (Item != null)
            {
                var File = Item as File;
                if (File == null)
                {
                    Error = $"{Path} is not a regular file";
                    return false;
                }

                File.Content = Encoding.UTF8.GetBytes(Text);
                File.Modified = DateTime.Now;
                return true;
            }

            var Parent = Fs.GetParentDirectory(Path);
            if (Parent == null)
            {
                Error = $"Parent directory for {Path} not found";
                return false;
            }

            Parent.Items.Add(new File(Path, 0, 0, 0x1A4, Encoding.UTF8.GetBytes(Text)));
            return true;
        }
    }

}
