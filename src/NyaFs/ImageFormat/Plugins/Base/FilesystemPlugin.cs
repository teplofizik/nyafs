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
        /// Filesystem type exposed to plugin-based content detection.
        /// Plugins that do not map to <see cref="ImageFormat.Types.FsType"/> keep Unknown.
        /// </summary>
        public virtual ImageFormat.Types.FsType FsType => ImageFormat.Types.FsType.Unknown;

        /// <summary>Options accepted by this filesystem. Values are stored in LinuxFilesystem.</summary>
        public virtual FilesystemOption[] Options => Array.Empty<FilesystemOption>();

        /// <summary>
        /// Validate and store a format-specific option in the target filesystem.
        /// Plugins can override this if they need extra validation.
        /// </summary>
        public virtual bool TryApplyOption(Elements.Fs.LinuxFilesystem Fs, string Name, string Value, out string Error)
        {
            Error = null;
            FilesystemOption Option = null;
            foreach (var O in Options)
            {
                if (string.Equals(O.Name, Name, StringComparison.OrdinalIgnoreCase))
                {
                    Option = O;
                    break;
                }
            }

            if (Option == null)
            {
                Error = "Unknown option '" + Name + "' for " + Format;
                return false;
            }

            string Normalized = Value;
            switch (Option.Type)
            {
                case FilesystemOptionType.Bool:
                    if (string.Equals(Value, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase))
                        Normalized = "true";
                    else if (string.Equals(Value, "0", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Value, "false", StringComparison.OrdinalIgnoreCase))
                        Normalized = "false";
                    else
                    {
                        Error = "Option '" + Name + "' expects true/false";
                        return false;
                    }
                    break;

                case FilesystemOptionType.Integer:
                    long Integer;
                    if (!Int64.TryParse(Value, out Integer))
                    {
                        Error = "Option '" + Name + "' expects an integer";
                        return false;
                    }
                    Normalized = Integer.ToString();
                    break;

                case FilesystemOptionType.Size:
                    long Size;
                    if (!TryParseSize(Value, out Size))
                    {
                        Error = "Option '" + Name + "' expects a size (e.g. 4096, 64K, 1M)";
                        return false;
                    }
                    Normalized = Size.ToString();
                    break;

                case FilesystemOptionType.Enum:
                    bool Allowed = false;
                    foreach (var V in Option.AllowedValues)
                        if (string.Equals(V, Value, StringComparison.OrdinalIgnoreCase))
                        {
                            Normalized = V;
                            Allowed = true;
                            break;
                        }
                    if (!Allowed)
                    {
                        Error = "Option '" + Name + "' expects one of: " + string.Join(", ", Option.AllowedValues);
                        return false;
                    }
                    break;
            }

            Fs.SetFilesystemOption(Format, Option.Name, Normalized);
            return true;
        }

        protected static bool TryParseSize(string Value, out long Size)
        {
            Size = 0;
            if (string.IsNullOrWhiteSpace(Value))
                return false;

            string Text = Value.Trim();
            long Multiplier = 1;
            char Suffix = Char.ToUpperInvariant(Text[Text.Length - 1]);
            if ((Suffix == 'K') || (Suffix == 'M') || (Suffix == 'G'))
            {
                Multiplier = (Suffix == 'K') ? 1024L : ((Suffix == 'M') ? 1024L * 1024 : 1024L * 1024 * 1024);
                Text = Text.Substring(0, Text.Length - 1);
            }

            long Parsed;
            if (!Int64.TryParse(Text, out Parsed) || (Parsed < 0))
                return false;

            Size = Parsed * Multiplier;
            return true;
        }

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
