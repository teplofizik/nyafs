using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NyaFs.ImageFormat;
using NyaFs.ImageFormat.Composite;
using NyaFs.Processor.Scripting.Helper;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// layout export {image} {schema.yaml} - export the build layout of a loaded image
    /// (slots, kinds, formats, options; immutable raw leaves are referenced by sha256).
    /// layout validate {schema.yaml} - parse the layout and check the format plugin.
    /// </summary>
    public class Layout : ScriptStepGenerator
    {
        public Layout() : base("layout")
        {
            AddConfig(new ScriptArgsConfig(0, new ScriptArgsParam[] {
                new Params.EnumScriptArgsParam("action", new[] { "export" }),
                new Params.StringScriptArgsParam("image"),
                new Params.LocalPathScriptArgsParam()
            }));

            AddConfig(new ScriptArgsConfig(1, new ScriptArgsParam[] {
                new Params.EnumScriptArgsParam("action", new[] { "validate" }),
                new Params.LocalPathScriptArgsParam()
            }));
        }

        public override ScriptStep Get(ScriptArgs Args)
        {
            if (Args.ArgConfig == 0)
                return new LayoutStep("export", Args.RawArgs[1], Args.RawArgs[2]);

            return new LayoutStep("validate", Args.RawArgs[1], null);
        }

        public class LayoutStep : ScriptStep
        {
            private readonly string Action;
            private readonly string Input;
            private readonly string Output;

            public LayoutStep(string Action, string Input, string Output) : base("layout")
            {
                this.Action = Action;
                this.Input = Input;
                this.Output = Output;
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                return (Action == "export") ? Export(Processor) : Validate(Processor);
            }

            private ScriptStepResult Export(ImageProcessor Processor)
            {
                var Filename = this.DetectFilePath(Input);
                if (Filename == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Input} not found!");

                var Data = System.IO.File.ReadAllBytes(Filename);
                var Tree = new ImageInspector(Processor.Plugins).Inspect(System.IO.Path.GetFileName(Filename), Data, Filename);

                var OutPath = System.IO.Path.GetFullPath(Output);
                var BlobDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(OutPath) ?? ".",
                    System.IO.Path.GetFileName(OutPath) + ".blobs");
                var Document = FromTree(Tree, BlobDir);

                System.IO.File.WriteAllText(OutPath, Document.ToYaml());
                return new ScriptStepResult(ScriptStepStatus.Ok,
                    $"Layout is exported to {Output} ({Document.Children.Count} slot(s)).");
            }

            private ScriptStepResult Validate(ImageProcessor Processor)
            {
                var Filename = this.DetectFilePath(Input);
                if (Filename == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"{Input} not found!");

                var Document = PlanDocument.Load(Filename, out var Error);
                if (Document == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Cannot parse layout: {Error}");

                if (String.IsNullOrEmpty(Document.Format))
                    return new ScriptStepResult(ScriptStepStatus.Error, "Layout has no format");

                PlanDocument.SetDocumentSource(Document.Options, Filename);

                var Plugin = Processor.Plugins.ResolveImage(Document.Format);
                if (Plugin == null)
                {
                    var FsPlugin = Processor.Plugins.ResolveFilesystem(Document.Format);
                    if (FsPlugin == null)
                        return new ScriptStepResult(ScriptStepStatus.Error,
                            $"Unknown container or filesystem format '{Document.Format}'");

                    var FsData = FilesystemImageBuilder.Build(Document, Processor.Plugins, out Error);
                    if (FsData == null)
                        return new ScriptStepResult(ScriptStepStatus.Error, $"Invalid layout: {Error}");

                    return new ScriptStepResult(ScriptStepStatus.Ok,
                        $"Layout is valid: {Document.Format}, {FsData.Length} bytes.");
                }

                if (!Plugin.SupportLayout)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Format '{Plugin.Format}' does not support layout");

                var Root = Plugin.CreatePlan(Document, out Error);
                if (Root == null)
                    return new ScriptStepResult(ScriptStepStatus.Error, $"Invalid layout: {Error}");

                return new ScriptStepResult(ScriptStepStatus.Ok,
                    $"Layout is valid: {Root.Format}, {Root.Children.Count} slot(s).");
            }
        }

        private static PlanDocument FromTree(ImageNode Root, string BlobDir)
        {
            var Doc = new PlanDocument
            {
                Schema = PlanDocument.CurrentSchema,
                Format = Root.Format ?? Root.Container ?? ""
            };

            foreach (var Option in Root.Options)
                Doc.Options[Option.Key] = Option.Value;

            foreach (var Child in Root.Children)
                Doc.Children.Add(FromNode(Child, BlobDir));

            return Doc;
        }

        private static PlanElement FromNode(ImageNode Node, string BlobDir)
        {
            var E = new PlanElement
            {
                Name = Node.Name,
                Kind = Node.Kind,
                Format = Node.Format,
                Offset = (Node.Offset >= 0) ? Node.Offset : (long?)null,
                Size = (Node.Size > 0) ? Node.Size : (long?)null
            };

            foreach (var Option in Node.Options)
                E.Options[Option.Key] = Option.Value;

            if (Node.Raw != null)
            {
                var Sha = Sha256Hex(Node.Raw);
                System.IO.Directory.CreateDirectory(BlobDir);
                var BlobPath = System.IO.Path.Combine(BlobDir, Sha);
                if (!System.IO.File.Exists(BlobPath))
                    System.IO.File.WriteAllBytes(BlobPath, Node.Raw);

                E.Immutable = true;
                E.Content = new PlanContent { File = System.IO.Path.GetFullPath(BlobPath) };
            }

            foreach (var Child in Node.Children)
                E.Children.Add(FromNode(Child, BlobDir));

            return E;
        }

        private static string Sha256Hex(byte[] Data)
        {
            using (var Sha = SHA256.Create())
            {
                var Hash = Sha.ComputeHash(Data);
                var Sb = new System.Text.StringBuilder(Hash.Length * 2);
                foreach (var B in Hash)
                    Sb.Append(B.ToString("x2"));
                return Sb.ToString();
            }
        }
    }
}
