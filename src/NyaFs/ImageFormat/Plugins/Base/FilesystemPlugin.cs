using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.ImageFormat.Plugins.Base
{
    /// <summary>
    /// Base filesystem plugin
    /// </summary>
    public class FilesystemPlugin : NyaPlugin
    {
        public readonly string FilesystemName;

        private bool Write;
        private bool Read;
        private bool Compression;

        public FilesystemPlugin(string Name, string FilesystemName, bool Read, bool Write, bool InternalCompression) : base(Name, "filesystem")
        {
            this.FilesystemName = FilesystemName;

            this.Read = Read;
            this.Write = Write;

            Compression = InternalCompression;
        }

        public ImageKind Kind => ImageKind.Filesystem;

        public string Format => FilesystemName;

        /// <summary>
        /// Is filesystem need compression in target image
        /// </summary>
        public virtual bool NeedExternalCompression => !Compression;

        /// <summary>
        /// Is read supported
        /// </summary>
        public virtual bool SupportRead => Read;

        /// <summary>
        /// Is write supported
        /// </summary>
        public virtual bool SupportWrite => Write;

        /// <summary>
        /// Is image contain this filesystem
        /// </summary>
        /// <param name="Image"></param>
        /// <returns></returns>
        public virtual bool DetectFilesystem(ImageData Image) => false;

        /// <summary>
        /// Get reader
        /// </summary>
        /// <returns></returns>
        public virtual Elements.Fs.Reader.Reader GetFilesystemReader(byte[] Data) => null;

        /// <summary>
        /// Get writer
        /// </summary>
        /// <returns></returns>
        public virtual Elements.Fs.Writer.Writer GetFilesystemWriter(Elements.Fs.LinuxFilesystem Fs) => null;

        /// <summary>
        /// Check is filesystem can save all data
        /// </summary>
        /// <param name="Fs"></param>
        /// <returns></returns>

        public virtual bool CheckFilesystem(Elements.Fs.LinuxFilesystem Fs) => true;

        /// <summary>
        /// Verify that every item's uid/gid fits the target format; log the first violation.
        /// </summary>
        protected bool CheckUidGid(Elements.Fs.LinuxFilesystem Fs, uint MaxUid, uint MaxGid)
        {
            string Bad = null;

            IterateAllFilesystem(Fs, I =>
            {
                if ((Bad == null) && (I.User > MaxUid)) Bad = $"user id {I.User}";
                if ((Bad == null) && (I.Group > MaxGid)) Bad = $"group id {I.Group}";
            });

            if (Bad != null)
            {
                Log.Error(0, $"{Bad} cannot be saved in {Format} image");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Verify that every item's name (single path component) fits the target format.
        /// </summary>
        protected bool CheckNameLength(Elements.Fs.LinuxFilesystem Fs, int MaxLen)
        {
            string Bad = null;

            IterateAllFilesystem(Fs, I =>
            {
                if (Bad == null)
                {
                    var Name = I.ShortFilename;
                    if ((Name != "/") && (Encoding.UTF8.GetByteCount(Name) > MaxLen))
                        Bad = Name;
                }
            });

            if (Bad != null)
            {
                Log.Error(0, $"name '{Bad}' is too long for {Format} image (max {MaxLen} bytes)");
                return false;
            }

            return true;
        }

        protected void IterateAllFilesystem(Elements.Fs.LinuxFilesystem Fs, Action<Filesystem.Universal.FilesystemItem> OnItem)
        {
            IterateDirectory(Fs.Fs.Root, OnItem);
        }

        private void IterateDirectory(Filesystem.Universal.Items.Dir Dir, Action<Filesystem.Universal.FilesystemItem> OnItem)
        {
            OnItem(Dir);

            foreach(var N in Dir.Items)
            {
                if (N.ItemType == Filesystem.Universal.Types.FilesystemItemType.Directory)
                    IterateDirectory(N as Filesystem.Universal.Items.Dir, OnItem);
                else
                    OnItem(N);
            }
        }
    }
}
