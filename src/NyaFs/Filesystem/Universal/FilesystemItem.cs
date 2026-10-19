using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Filesystem.Universal
{
    public class FilesystemItem
    {
        private static string PreprocessFilename(string Filename)
        {
            if (Filename.Length > 1)
            {
                if ((Filename[0] == '\\') || (Filename[0] == '/'))
                    return Filename.Substring(1);
            }

            return Filename;
        }

        public FilesystemItem(Types.FilesystemItemType Type, string Filename, uint User, uint Group, uint Mode)
        {
            ItemType = Type;
            Modified = DateTime.Now;

            this.User = User;
            this.Group = Group;
            this.Mode = Mode;

            this.Filename = Filename;
        }

        private string IntFilename;

        public readonly Types.FilesystemItemType ItemType;

        public uint User;
        public uint Group;

        public uint Mode; // Access rights 12 bit 4 4 4

        /// <summary>Extended attributes (full name -> raw value). Empty when the item has none.</summary>
        public readonly Dictionary<string, byte[]> Xattrs = new Dictionary<string, byte[]>();

        public string Filename
        {
            get { return IntFilename; }
            set { IntFilename = PreprocessFilename(value); }
        }

        public string ShortFilename
        {
            get
            {
                var Idx = Filename.LastIndexOf('/');
                return (Idx > 0) ? Filename.Substring(Idx + 1) : Filename;
            }
        }

        public virtual long Size => 0;

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime Created = UnixEpoch;
        public DateTime Modified = UnixEpoch;

        public uint FullMode
        {
            get
            {
                uint Res = Mode;

                switch(ItemType)
                {
                    case Types.FilesystemItemType.Fifo: Res |= 0x1000; break;
                    case Types.FilesystemItemType.Character: Res |= 0x2000; break;
                    case Types.FilesystemItemType.Directory: Res |= 0x4000; break;
                    case Types.FilesystemItemType.Block: Res |= 0x6000; break;
                    case Types.FilesystemItemType.File: Res |= 0x8000; break;
                    case Types.FilesystemItemType.SymLink: Res |= 0xA000; break;
                    case Types.FilesystemItemType.Socket: Res |= 0xC000; break;
                }

                return Res;
            }
        }
    }
}
