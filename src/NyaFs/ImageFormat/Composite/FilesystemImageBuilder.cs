using System;
using NyaFs.ImageFormat.Plugins;

namespace NyaFs.ImageFormat.Composite
{
    /// <summary>
    /// Builds a standalone filesystem image from a layout whose root format is a
    /// filesystem (squashfs, ext4, fat, ...). The content is the root `content.dir`;
    /// filesystem parameters live in root `options`.
    /// </summary>
    public static class FilesystemImageBuilder
    {
        public static byte[] Build(PlanDocument Document, PluginRegistry Plugins, out string Error)
        {
            Error = null;

            if ((Document == null) || String.IsNullOrEmpty(Document.Format))
            {
                Error = "Layout has no filesystem format";
                return null;
            }

            var Plugin = Plugins?.ResolveFilesystem(Document.Format);
            if (Plugin == null)
            {
                Error = $"Unknown filesystem format '{Document.Format}'";
                return null;
            }

            if (!Plugin.SupportWrite)
            {
                Error = $"Filesystem '{Document.Format}' is read-only";
                return null;
            }

            var Content = Document.Content;
            if ((Content == null) || String.IsNullOrEmpty(Content.Dir))
            {
                Error = $"Filesystem layout '{Document.Format}' needs content: {{ dir: ... }}";
                return null;
            }

            Document.Options.TryGetValue("plan.base", out var BaseDir);
            var DirPath = PlanContentResolver.ResolvePath(Content.Dir, BaseDir);
            if (!System.IO.Directory.Exists(DirPath))
            {
                Error = $"Content directory '{DirPath}' is not found";
                return null;
            }

            var Fs = PlanContentResolver.BuildFilesystemObject(Document.Format, DirPath, Content, out Error);
            if (Fs == null)
                return null;

            var Options = new System.Collections.Generic.Dictionary<string, string>(
                Document.Options, StringComparer.OrdinalIgnoreCase);
            Options.Remove("size");
            Options.Remove("fill");

            if (!FilesystemPlanOptions.Apply(Fs, Document.Format, Options, Plugins, out Error))
                return null;

            var Data = PlanContentResolver.SerializeFilesystem(Fs, Document.Format, Plugins, out Error);
            if (Data == null)
                return null;

            if (!String.IsNullOrWhiteSpace(Content.Compression))
            {
                if (!PlanCompression.TryApply(Data, Content.Compression, Plugins, out var Compressed, out Error))
                    return null;

                Data = Compressed;
            }

            var Target = Document.Size;
            if (!Target.HasValue && Document.Options.TryGetValue("size", out var SizeText))
                Target = PlanDocument.ParseSize(SizeText, out _);
            if (!Target.HasValue && (Fs.FsSize > 0))
                Target = Fs.FsSize;

            if (!Target.HasValue)
                return Data;

            if ((Fs.FsSize > 0) && (Fs.FsSize > Target.Value))
            {
                Error = $"fs-size 0x{Fs.FsSize:x} is larger than the target size 0x{Target.Value:x}";
                return null;
            }

            if (Data.Length > Target.Value)
            {
                Error = $"Filesystem image is 0x{Data.Length:x} bytes, larger than the target size 0x{Target.Value:x}";
                return null;
            }

            if (Data.Length == Target.Value)
                return Data;

            if (Target.Value > Int32.MaxValue)
            {
                Error = $"Target size 0x{Target.Value:x} is too large for a filesystem image";
                return null;
            }

            var Result = new byte[(int)Target.Value];
            Buffer.BlockCopy(Data, 0, Result, 0, Data.Length);

            var Fill = GetFill(Document, Document.Format);
            for (var I = Data.Length; I < Result.Length; I++)
                Result[I] = Fill;

            return Result;
        }

        public static bool BuildTo(System.IO.Stream Output, PlanDocument Document,
            PluginRegistry Plugins, out string Error)
        {
            Error = null;

            if ((Document == null) || String.IsNullOrEmpty(Document.Format))
            {
                Error = "Layout has no filesystem format";
                return false;
            }

            var Content = Document.Content;
            if ((Content == null) || String.IsNullOrEmpty(Content.Dir))
            {
                Error = $"Filesystem layout '{Document.Format}' needs content: {{ dir: ... }}";
                return false;
            }

            // Compression currently works on the completed image.
            if (!String.IsNullOrWhiteSpace(Content.Compression))
            {
                var Data = Build(Document, Plugins, out Error);
                if (Data == null)
                    return false;

                Output.Write(Data, 0, Data.Length);
                return true;
            }

            var Plugin = Plugins?.ResolveFilesystem(Document.Format);
            if (Plugin == null)
            {
                Error = $"Unknown filesystem format '{Document.Format}'";
                return false;
            }

            if (!Plugin.SupportWrite)
            {
                Error = $"Filesystem '{Document.Format}' is read-only";
                return false;
            }

            Document.Options.TryGetValue("plan.base", out var BaseDir);
            var DirPath = PlanContentResolver.ResolvePath(Content.Dir, BaseDir);
            if (!System.IO.Directory.Exists(DirPath))
            {
                Error = $"Content directory '{DirPath}' is not found";
                return false;
            }

            var Fs = PlanContentResolver.BuildFilesystemObject(Document.Format, DirPath, Content, out Error);
            if (Fs == null)
                return false;

            var Options = new System.Collections.Generic.Dictionary<string, string>(
                Document.Options, StringComparer.OrdinalIgnoreCase);
            Options.Remove("size");
            Options.Remove("fill");

            if (!FilesystemPlanOptions.Apply(Fs, Document.Format, Options, Plugins, out Error))
                return false;

            var Target = Document.Size;
            if (!Target.HasValue && Document.Options.TryGetValue("size", out var SizeText))
                Target = PlanDocument.ParseSize(SizeText, out _);
            if (!Target.HasValue && (Fs.FsSize > 0))
                Target = Fs.FsSize;

            if (Target.HasValue && (Fs.FsSize > 0) && (Fs.FsSize > Target.Value))
            {
                Error = $"fs-size 0x{Fs.FsSize:x} is larger than the target size 0x{Target.Value:x}";
                return false;
            }

            var Writer = Plugin.GetFilesystemWriter(Fs);
            if (Writer == null)
            {
                Error = $"No filesystem writer for '{Document.Format}'";
                return false;
            }

            long Count;
            if (Output.CanSeek)
            {
                if (!Writer.WriteFsTo(Output, Fs))
                {
                    Error = $"Filesystem '{Document.Format}' produced no data";
                    return false;
                }

                Count = Output.Length;
            }
            else
            {
                var Counter = new CountingStream(Output);
                if (!Writer.WriteFsTo(Counter, Fs))
                {
                    Error = $"Filesystem '{Document.Format}' produced no data";
                    return false;
                }

                Count = Counter.Length;
            }

            if (Target.HasValue)
            {
                if (Count > Target.Value)
                {
                    Error = $"Filesystem image is 0x{Count:x} bytes, larger than the target size 0x{Target.Value:x}";
                    return false;
                }

                var Fill = GetFill(Document, Document.Format);
                var Buffer = new byte[64 * 1024];
                for (var I = 0; I < Buffer.Length; I++)
                    Buffer[I] = Fill;

                if (Output.CanSeek)
                    Output.Seek(Count, System.IO.SeekOrigin.Begin);

                var Remaining = Target.Value - Count;
                while (Remaining > 0)
                {
                    var Chunk = (int)Math.Min(Remaining, Buffer.Length);
                    Output.Write(Buffer, 0, Chunk);
                    Remaining -= Chunk;
                }
            }

            return true;
        }

        private sealed class CountingStream : System.IO.Stream
        {
            private readonly System.IO.Stream Target;
            private long Count;

            public CountingStream(System.IO.Stream Target) { this.Target = Target; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => Count;
            public override long Position { get => Count; set => throw new NotSupportedException(); }
            public override void Write(byte[] Buffer, int Offset, int Count) { Target.Write(Buffer, Offset, Count); this.Count += Count; }
            public override void Flush() => Target.Flush();
            public override int Read(byte[] Buffer, int Offset, int Count) => throw new NotSupportedException();
            public override long Seek(long Offset, System.IO.SeekOrigin Origin) => throw new NotSupportedException();
            public override void SetLength(long Value) => throw new NotSupportedException();
        }
        private static byte GetFill(PlanDocument Document, string Format)
        {
            if (Document.Options.TryGetValue("fill", out var Text))
            {
                var Value = PlanDocument.ParseSize(Text, out _);
                if (Value.HasValue)
                    return (byte)Value.Value;
            }

            switch ((Format ?? "").ToLowerInvariant())
            {
                case "ubifs":
                case "jffs2":
                case "yaffs2":
                    return 0xFF;
                default:
                    return 0x00;
            }
        }
    }
}
