using System;
using System.IO;
using System.Text;

namespace NyaFs.ImageFormat.Elements.Fs.Reader
{
    public class BaseFsReader : Reader
    {
        protected Filesystem.Universal.IFilesystemReader FsReader;
        Types.FsType FsType;
        long FsSize;

        public BaseFsReader(Types.FsType FsType, Filesystem.Universal.IFilesystemReader FsReader, long FsSize = 0)
        {
            this.FsType = FsType;
            this.FsReader = FsReader;
            this.FsSize = FsSize;
        }

        /// <summary>
        /// Читаем в файловую систему из cpio-файла
        /// </summary>
        /// <param name="Dst"></param>
        public override void ReadToFs(LinuxFilesystem Dst)
        {
            ImportDir(Dst.Fs.Root, "/");

            // Preserve the root directory's own owner/permissions when the reader can report them.
            if ((FsReader is Filesystem.Universal.IRootFsInfo RootInfo) &&
                RootInfo.TryGetRootFsItem(out uint RootUser, out uint RootGroup, out uint RootMode))
            {
                Dst.Fs.Root.User = RootUser;
                Dst.Fs.Root.Group = RootGroup;
                Dst.Fs.Root.Mode = RootMode;
            }

            if (FsReader is Filesystem.Universal.IRootXattrs RootXattrs)
            {
                var Xattrs = RootXattrs.GetRootXattrs();
                if (Xattrs != null)
                {
                    foreach (var X in Xattrs)
                        Dst.Fs.Root.Xattrs[X.Key] = X.Value;
                }
            }

            if (Dst.Info.Type == Types.ImageType.IH_TYPE_INVALID)
                Dst.Info.Type = Types.ImageType.IH_TYPE_RAMDISK;

            Dst.FilesystemType = FsType;
            Dst.FsSize = FsSize;
        }

        private void ImportDir(Filesystem.Universal.Items.Dir Dir, string Path)
        {
            var Elements = FsReader.ReadDir(Path);
            if (Elements == null)
                return;

            foreach (var E in Elements)
            {
                Filesystem.Universal.FilesystemItem Created = null;

                switch (E.NodeType)
                {
                    case Filesystem.Universal.Types.FilesystemItemType.Directory:
                        {
                            var SubDir = new Filesystem.Universal.Items.Dir(E.Path, E.User, E.Group, E.HexMode);
                            Dir.Items.Add(SubDir);
                            Created = SubDir;
                            ImportDir(SubDir, E.Path);
                        }
                        break;
                    case Filesystem.Universal.Types.FilesystemItemType.File:
                        {
                            var Data = FsReader.Read(E.Path) ?? new byte[0];
                            Created = new Filesystem.Universal.Items.File(E.Path, E.User, E.Group, E.HexMode, Data);
                            Dir.Items.Add(Created);
                        }
                        break;
                    case Filesystem.Universal.Types.FilesystemItemType.SymLink:
                        {
                            Created = new Filesystem.Universal.Items.SymLink(E.Path, E.User, E.Group, E.HexMode, FsReader.ReadLink(E.Path));
                            Dir.Items.Add(Created);
                        }
                        break;
                    case Filesystem.Universal.Types.FilesystemItemType.Fifo:
                        {
                            Created = new Filesystem.Universal.Items.Fifo(E.Path, E.User, E.Group, E.HexMode);
                            Dir.Items.Add(Created);
                        }
                        break;
                    case Filesystem.Universal.Types.FilesystemItemType.Character:
                        {
                            var DevInfo = FsReader.ReadDevice(E.Path);
                            var Char = new Filesystem.Universal.Items.Char(E.Path, E.User, E.Group, E.HexMode);
                            Char.Major = DevInfo?.Major ?? 0;
                            Char.Minor = DevInfo?.Minor ?? 0;
                            Created = Char;
                            Dir.Items.Add(Char);
                        }
                        break;
                    case Filesystem.Universal.Types.FilesystemItemType.Block:
                        {
                            var DevInfo = FsReader.ReadDevice(E.Path);
                            var Block = new Filesystem.Universal.Items.Block(E.Path, E.User, E.Group, E.HexMode);
                            Block.Major = DevInfo?.Major ?? 0;
                            Block.Minor = DevInfo?.Minor ?? 0;
                            Created = Block;
                            Dir.Items.Add(Block);
                        }
                        break;
                }

                if ((Created != null) && (E.Xattrs != null))
                {
                    foreach (var X in E.Xattrs)
                        Created.Xattrs[X.Key] = X.Value;
                }
            }
        }
    }
}