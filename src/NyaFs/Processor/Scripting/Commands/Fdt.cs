using System;
using System.Collections.Generic;
using System.Text;
using NyaFs.FlattenedDeviceTree;
using NyaFs.Processor.Scripting.Helper;
using FdtTree = NyaFs.FlattenedDeviceTree.FlattenedDeviceTree;

namespace NyaFs.Processor.Scripting.Commands
{
    /// <summary>
    /// Inspect and edit the loaded devicetree:
    ///   fdt get &lt;path&gt; [property]
    ///   fdt set &lt;path&gt; &lt;property&gt; [value]
    ///   fdt add &lt;path&gt;
    ///   fdt del &lt;path&gt; [property]
    ///   fdt apply &lt;overlay.dtbo&gt;
    /// Paths may be absolute ("/soc/uart@0") or labels ("&amp;uart0").
    /// Values use dtc syntax: "text", &lt;0x1 0x2&gt;, [00 ff].
    /// </summary>
    public class Fdt : ScriptStepGenerator
    {
        public Fdt() : base("fdt")
        {
            AddConfig(new ScriptArgsConfig(0, null));
        }

        public override ScriptStep Get(ScriptArgs Args) => new FdtScriptStep(Args.RawArgs);

        public class FdtScriptStep : ScriptStep
        {
            private readonly string[] Args;

            public FdtScriptStep(string[] Args) : base("fdt")
            {
                this.Args = Args ?? Array.Empty<string>();
            }

            public override ScriptStepResult Exec(ImageProcessor Processor)
            {
                if (Args.Length == 0)
                    return Error("Usage: fdt get|set|add|del|apply ...");

                var Dtb = Processor.GetDevTree();
                if ((Dtb == null) || !Dtb.Loaded)
                    return Error("Device tree is not loaded");

                var Tree = Dtb.DevTree;
                switch (Args[0].ToLowerInvariant())
                {
                    case "get": return Get(Tree);
                    case "set": return Set(Processor, Tree);
                    case "add": return Add(Processor, Tree);
                    case "del":
                    case "delete":
                    case "rm": return Delete(Processor, Tree);
                    case "apply": return Apply(Processor, Tree);
                    default: return Error($"Unknown fdt subcommand '{Args[0]}'");
                }
            }

            private ScriptStepResult Get(FdtTree Tree)
            {
                if ((Args.Length < 2) || (Args.Length > 3))
                    return Error("Usage: fdt get <path> [property]");

                var Node = Tree.Resolve(Args[1]);
                if (Node == null)
                    return Error($"Node '{Args[1]}' is not found");

                if (Args.Length == 3)
                {
                    var Property = Node.GetProperty(Args[2]);
                    if (Property == null)
                        return Error($"Property '{Args[2]}' is not found in '{Args[1]}'");

                    Log.Write(0, FdtValue.Format(Property.Value));
                    return Ok($"Get {Args[1]}:{Args[2]}");
                }

                var Path = (Args[1] == "/") ? "" : Args[1];
                Log.Write(0, $"{Path} {{");
                foreach (var Property in Node.Properties)
                {
                    var Value = FdtValue.Format(Property.Value);
                    Log.Write(0, (Value.Length == 0) ? $"  {Property.Name};" : $"  {Property.Name} = {Value};");
                }
                foreach (var Child in Node.Nodes)
                    Log.Write(0, $"  {Child.Name} {{...}}");
                Log.Write(0, "};");

                return Ok($"Get {Args[1]}");
            }

            private ScriptStepResult Set(ImageProcessor Processor, FdtTree Tree)
            {
                if (Args.Length < 3)
                    return Error("Usage: fdt set <path> <property> [value]");

                var Node = Tree.Resolve(Args[1]);
                if (Node == null)
                    return Error($"Node '{Args[1]}' is not found");

                string Text = (Args.Length > 3) ? String.Join(" ", Args, 3, Args.Length - 3) : "";
                if (!FdtValue.TryParse(Text, out var Value, out var ValueError))
                    return Error(ValueError);

                var Property = Node.GetProperty(Args[2]);
                if (Property != null)
                    Property.Value = Value;
                else
                    Node.Properties.Add(new FlattenedDeviceTree.Types.Property(Args[2], Value));

                Processor.MarkDirty(ImageFormat.ImageKind.Devtree);
                return Ok($"Set {Args[1]}:{Args[2]}");
            }

            private ScriptStepResult Add(ImageProcessor Processor, FdtTree Tree)
            {
                if (Args.Length != 2)
                    return Error("Usage: fdt add <path>");

                if (Tree.Resolve(Args[1]) != null)
                    return Error($"Node '{Args[1]}' already exists");

                string Name;
                var Parent = Tree.ResolveParent(Args[1], out Name);
                if ((Parent == null) || (Name == null))
                    return Error($"Parent of '{Args[1]}' is not found");

                Parent.Nodes.Add(new FlattenedDeviceTree.Types.Node(Name));
                Processor.MarkDirty(ImageFormat.ImageKind.Devtree);
                return Ok($"Added node {Args[1]}");
            }

            private ScriptStepResult Delete(ImageProcessor Processor, FdtTree Tree)
            {
                if ((Args.Length < 2) || (Args.Length > 3))
                    return Error("Usage: fdt del <path> [property]");

                var Node = Tree.Resolve(Args[1]);
                if (Node == null)
                    return Error($"Node '{Args[1]}' is not found");

                if (Args.Length == 3)
                {
                    if (Node.Properties.RemoveAll(P => P.Name == Args[2]) == 0)
                        return Error($"Property '{Args[2]}' is not found in '{Args[1]}'");

                    Processor.MarkDirty(ImageFormat.ImageKind.Devtree);
                    return Ok($"Deleted {Args[1]}:{Args[2]}");
                }

                string Name;
                var Parent = Tree.ResolveParent(Args[1], out Name);
                if ((Parent == null) || (Name == null))
                    return Error($"Cannot delete '{Args[1]}'");

                if (Parent.Nodes.RemoveAll(N => N.Name == Name) == 0)
                    return Error($"Node '{Args[1]}' is not found");

                Processor.MarkDirty(ImageFormat.ImageKind.Devtree);
                return Ok($"Deleted node {Args[1]}");
            }

            private ScriptStepResult Apply(ImageProcessor Processor, FdtTree Tree)
            {
                if (Args.Length != 2)
                    return Error("Usage: fdt apply <overlay.dtbo>");

                var Filename = this.DetectFilePath(Args[1]);
                if (Filename == null)
                    return Error($"{Args[1]} not found!");

                byte[] Data;
                try
                {
                    Data = System.IO.File.ReadAllBytes(Filename);
                }
                catch (Exception E)
                {
                    return Error($"Cannot read {Args[1]}: {E.Message}");
                }

                var Reader = new FlattenedDeviceTree.Reader.FDTReader(Data);
                if (!Reader.Correct)
                    return Error($"{Args[1]} is not a valid devicetree blob");

                var Overlay = Reader.Read();
                if (!FdtOverlay.TryApply(Tree, Overlay, out var ErrorText))
                    return Error($"Cannot apply overlay: {ErrorText}");

                Processor.MarkDirty(ImageFormat.ImageKind.Devtree);
                return Ok($"Applied overlay {Args[1]}");
            }

            private ScriptStepResult Ok(string Text) =>
                new ScriptStepResult(ScriptStepStatus.Ok, Text);

            private ScriptStepResult Error(string Text) =>
                new ScriptStepResult(ScriptStepStatus.Error, Text);
        }
    }
}
