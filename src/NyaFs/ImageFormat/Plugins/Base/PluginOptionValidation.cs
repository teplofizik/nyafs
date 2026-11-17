using System;

namespace NyaFs.ImageFormat.Plugins.Base
{
    internal static class PluginOptionValidation
    {
        public static bool Validate(FilesystemOption Option, string Value, out string Normalized, out string Error)
        {
            Normalized = Value;
            Error = null;

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
                        Error = "Option '" + Option.Name + "' expects true/false";
                        return false;
                    }
                    return true;

                case FilesystemOptionType.Integer:
                    long Integer;
                    if (!Int64.TryParse(Value, out Integer))
                    {
                        Error = "Option '" + Option.Name + "' expects an integer";
                        return false;
                    }
                    Normalized = Integer.ToString();
                    return true;

                case FilesystemOptionType.Size:
                    long Size;
                    if (!TryParseSize(Value, out Size))
                    {
                        Error = "Option '" + Option.Name + "' expects a size (e.g. 4096, 64K, 1M)";
                        return false;
                    }
                    Normalized = Size.ToString();
                    return true;

                case FilesystemOptionType.Enum:
                    foreach (var V in Option.AllowedValues)
                    {
                        if (string.Equals(V, Value, StringComparison.OrdinalIgnoreCase))
                        {
                            Normalized = V;
                            return true;
                        }
                    }
                    Error = "Option '" + Option.Name + "' expects one of: " + string.Join(", ", Option.AllowedValues);
                    return false;

                default:
                    return true;
            }
        }

        public static bool TryParseSize(string Value, out long Size)
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
    }
}
