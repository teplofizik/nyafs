using System;
using System.Collections.Generic;
using System.IO;
using NyaFs.Filesystem.Universal;
using NyaFs.Filesystem.Universal.Items;
using NyaFs.ImageFormat.Elements.Fs;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Resolves the declarative `content` of a plan node into bytes:
    /// file (read as-is), dir (build a filesystem in memory), blob (content-addressed
    /// cache) or empty (leave erased). The optional compression for archive-style
    /// filesystems is applied by the caller if needed.
    /// </summary>
    public static class PlanContentResolver
    {
        public static bool TryResolve(ImageBuildNode Node, PluginRegistry Plugins,
            string BaseDir, string BlobDir, out byte[] Data, out bool Skip, out string Error)
        {
            Data = null;
            Skip = false;
            Error = null;

            if ((Node.Content == null) || Node.Dirty)
                return false;

            var C = Node.Content;

            if (C.Empty)
            {
                Skip = true;
                return true;
            }

            if (!String.IsNullOrEmpty(C.File))
            {
                var FilePath = ResolvePath(C.File, BaseDir);
                if (!System.IO.File.Exists(FilePath))
                {
                    Error = $"Content file '{FilePath}' for '{Node.Path}' is not found";
                    return true;
                }

                Data = System.IO.File.ReadAllBytes(FilePath);
                return true;
            }

            if (!String.IsNullOrEmpty(C.Blob))
            {
                var Sha = C.Blob.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? C.Blob.Substring(7) : C.Blob;

                foreach (var Dir in BlobDirectories(BaseDir, BlobDir))
                {
                    var BlobPath = System.IO.Path.Combine(Dir, Sha);
                    if (System.IO.File.Exists(BlobPath))
                    {
                        Data = System.IO.File.ReadAllBytes(BlobPath);
                        return true;
                    }
                }

                Error = $"Blob '{Sha}' for '{Node.Path}' is not found in the local cache";
                return true;
            }

            if (!String.IsNullOrEmpty(C.Dir))
            {
                var DirPath = ResolvePath(C.Dir, BaseDir);
                if (!Directory.Exists(DirPath))
                {
                    Error = $"Content directory '{DirPath}' for '{Node.Path}' is not found";
                    return true;
                }

                var Format = !String.IsNullOrEmpty(Node.Format) ? Node.Format : GetOption(C, "fs", null);
                if (String.IsNullOrEmpty(Format))
                {
                    Error = $"Content directory for '{Node.Path}' needs a filesystem format";
                    return true;
                }

                Data = BuildFilesystem(Format, DirPath, C, Plugins, out Error);
                return true;
            }

            Error = $"Content of '{Node.Path}' has no source (file/dir/blob/empty)";
            return true;
        }

        private static string ResolvePath(string Path, string BaseDir)
        {
            if (String.IsNullOrEmpty(Path))
                return Path;

            if (System.IO.Path.IsPathRooted(Path) || String.IsNullOrEmpty(BaseDir))
                return Path;

            return System.IO.Path.Combine(BaseDir, Path);
        }

        private static IEnumerable<string> BlobDirectories(string BaseDir, string BlobDir)
        {
            if (!String.IsNullOrEmpty(BlobDir))
                yield return BlobDir;

            if (!String.IsNullOrEmpty(BaseDir))
                yield return BaseDir;

            var Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrEmpty(Local))
                yield return System.IO.Path.Combine(Local, "NyaFs", "blobs");
        }

        private static byte[] BuildFilesystem(string Format, string Directory, PlanContent Content,
            PluginRegistry Plugins, out string Error)
        {
            Error = null;

            var Fs = new LinuxFilesystem { FilesystemFormat = Format };

            var User = ParseNumber(GetOption(Content, "user", null), 0);
            var Group = ParseNumber(GetOption(Content, "group", null), 0);
            var DirMode = ParseMode(GetOption(Content, "dir-mode", null), 0x1ED);  // 0755
            var FileMode = ParseMode(GetOption(Content, "file-mode", null), 0x1A4); // 0644

            try
            {
                ImportDirectory(Fs.Fs.Root, Directory, User, Group, DirMode, FileMode);
            }
            catch (Exception E)
            {
                Error = "Cannot read content directory: " + E.Message;
                return null;
            }

            var Plugin = Plugins?.ResolveFilesystem(Format);
            var Writer = Plugin?.GetFilesystemWriter(Fs);
            if (Writer == null)
            {
                Error = $"No filesystem writer for '{Format}'";
                return null;
            }

            if (!Writer.CheckFilesystem(Fs))
            {
                Error = $"Filesystem ''{Format}'' cannot represent the content of the directory";
                return null;
            }

            Writer.WriteFs(Fs);
            if (!Writer.HasRawStreamData)
            {
                Error = $"Filesystem '{Format}' produced no data";
                return null;
            }

            return Writer.RawStream;
        }

        private static void ImportDirectory(Dir Parent, string LocalDir,
            uint User, uint Group, uint DirMode, uint FileMode)
        {
            foreach (var Entry in System.IO.Directory.GetFileSystemEntries(LocalDir))
            {
                var Name = System.IO.Path.GetFileName(Entry);
                var FsPath = NyaFs.Filesystem.Universal.Helper.FsHelper.CombinePath(Parent.Filename, Name);

                FileSystemInfo Info = System.IO.Directory.Exists(Entry)
                    ? (FileSystemInfo)new DirectoryInfo(Entry)
                    : new FileInfo(Entry);

#if NET6_0_OR_GREATER
                var Link = Info.LinkTarget;
                if (Link != null)
                {
                    Parent.Items.Add(new SymLink(FsPath, User, Group, 0x1FF, Link));
                    continue;
                }
#endif
                if (Info is DirectoryInfo)
                {
                    var Child = new Dir(FsPath, User, Group, DirMode);
                    Parent.Items.Add(Child);
                    ImportDirectory(Child, Entry, User, Group, DirMode, FileMode);
                }
                else
                {
                    Parent.Items.Add(new NyaFs.Filesystem.Universal.Items.File(FsPath, User, Group, FileMode,
                        System.IO.File.ReadAllBytes(Entry)));
                }
            }
        }

        private static string GetOption(PlanContent Content, string Key, string Default) =>
            Content.Options.TryGetValue(Key, out var Value) ? Value : Default;

        private static uint ParseNumber(string Text, uint Default)
        {
            if (String.IsNullOrWhiteSpace(Text))
                return Default;

            var Value = PlanDocument.ParseSize(Text, out var Auto);
            return (Auto || !Value.HasValue) ? Default : (uint)Value.Value;
        }

        private static uint ParseMode(string Text, uint Default)
        {
            if (String.IsNullOrWhiteSpace(Text))
                return Default;

            var Value = Text.Trim();
            if (Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint Hex;
                return UInt32.TryParse(Value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out Hex) ? Hex : Default;
            }

            if ((Value.Length == 3) || (Value.Length == 4))
            {
                var Digits = true;
                foreach (var C in Value)
                    if ((C < '0') || (C > '7'))
                        Digits = false;

                if (Digits)
                    return Convert.ToUInt32(Value, 8);
            }

            uint Dec;
            return UInt32.TryParse(Value, out Dec) ? Dec : Default;
        }
    }
}
