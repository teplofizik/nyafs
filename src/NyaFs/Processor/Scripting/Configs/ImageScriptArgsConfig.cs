using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NyaFs.Processor.Scripting.Configs
{
    internal class ImageScriptArgsConfig : ScriptArgsConfig
    {
        private string ImgType;

        /// <summary>
        /// Well-known formats, used only as a hint in the error message. The actual format is
        /// free-form and is validated at execution time against the built-in writers and the
        /// registered filesystem/kernel/devtree plugins (so plugin-provided formats work
        /// without touching this list).
        /// </summary>
        private string[] KnownFormats;

        public ImageScriptArgsConfig(int Id, string ImgType, string[] KnownFormats) : base(Id, new ScriptArgsParam[] {
                    new Params.FsPathScriptArgsParam(),
                    new Params.EnumScriptArgsParam("type", new string[] { ImgType }),
                    new Params.StringScriptArgsParam("format")
                })
        {
            this.ImgType = ImgType;
            this.KnownFormats = KnownFormats;
        }

        public override bool IsMyConfig(string[] Args) => (Args.Length > 1) && (ImgType == Args[1]);

        public override bool CheckArgs(string[] Args)
        {
            if (Args.Length != 3)
            {
                if (Args.Length < 3)
                    Log.Error(0, $"Please specify image format. Known formats: {String.Join(", ", KnownFormats)}");
                if (Args.Length > 3)
                    Log.Error(0, $"There are many arguments. Must be <filename> <imagetype> <format>");

                return false;
            }

            return true;
        }
    }
}
