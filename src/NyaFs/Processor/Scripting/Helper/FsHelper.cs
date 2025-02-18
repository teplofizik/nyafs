﻿using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.Processor.Scripting.Helper
{
    public static class FsHelper
    {
        public static string DetectDirPath(this ScriptStep S, string Path)
        {
            if (System.IO.File.Exists(Path))
                return Path;

            var RelPath = System.IO.Path.Combine(S?.ScriptPath ?? "", Path);
            if (System.IO.Directory.Exists(RelPath))
                return RelPath;

            return null;
        }

        public static string DetectFilePath(this ScriptStep S, string Path)
        {
            if (System.IO.File.Exists(Path))
                return Path;

            var RelPath = System.IO.Path.Combine(S?.ScriptPath ?? "", Path);
            if(System.IO.File.Exists(RelPath))
                return RelPath;

            return null;
        }

        private static Filesystem.Universal.FilesystemItem GetItem(ImageFormat.Elements.Fs.LinuxFilesystem Fs, string Path)
        {
            try
            {
                return Fs.GetElement(Path);
            }
            catch(Exception)
            {
                return null;
            }
        }

        public static Filesystem.Universal.FilesystemItem GetItem(ImageFormat.Elements.Fs.LinuxFilesystem Fs, string Base, string Path)
        {
            if (Path == "")
                return null;

            // TODO: Simplify path with ..
            if (Path == "..")
                return Fs.GetParentDirectory(Base);

            if (Path[0] == '/')
                // Provided absolute path
                return GetItem(Fs, Path);
            else
                return GetItem(Fs, CombinePath(Base, Path));
        }

        public static string CombinePath(string Base, string Name)
        {
            if ((Base == "/") || (Base == ".")) return Name;

            return Base + "/" + Name;
        }

    }
}
