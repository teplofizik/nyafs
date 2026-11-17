using System;

namespace NyaFs.ImageFormat.Plugins.Base
{
    public enum FilesystemOptionType
    {
        String,
        Bool,
        Integer,
        Size,
        Enum
    }

    /// <summary>
    /// Declarative option exposed by a filesystem plugin. External plugins use the same
    /// descriptor as built-in filesystems, so the CLI can validate and store values generically.
    /// </summary>
    public sealed class FilesystemOption
    {
        public readonly string Name;
        public readonly string Description;
        public readonly FilesystemOptionType Type;
        public readonly string DefaultValue;
        public readonly string[] AllowedValues;

        public FilesystemOption(string Name, FilesystemOptionType Type, string DefaultValue = null,
            string Description = null, string[] AllowedValues = null)
        {
            this.Name = Name;
            this.Type = Type;
            this.DefaultValue = DefaultValue;
            this.Description = Description;
            this.AllowedValues = AllowedValues ?? Array.Empty<string>();
        }
    }
}
