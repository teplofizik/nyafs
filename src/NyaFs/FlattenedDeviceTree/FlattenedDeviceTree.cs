using System;
using System.Collections.Generic;
using System.Text;

namespace NyaFs.FlattenedDeviceTree
{
    public class FlattenedDeviceTree
    {
        public Types.Node Root = new Types.Node();


        public uint CpuId = 0;

        /// <summary>
        /// Зарезре
        /// </summary>
        public Types.ReservedMemory[] ReserveMemory = new Types.ReservedMemory[] { };

        public Types.Node Get(string Path)
        {
            if (Path == ".") return Root;

            if (Path[0] == '/') Path = Path.Substring(1);
            var Parts = Path.Split('/');

            Types.Node Base = Root;
            for(int i = 0; i < Parts.Length; i++)
            {
                var P = Parts[i];
                bool Found = false;
                foreach (var I in Base.Nodes)
                {
                    if (I.Name == P)
                    {
                        Base = I;
                        Found = true;
                        if (i == Parts.Length - 1)
                            return I;
                        else
                            break;
                    }
                }

                if(!Found)
                    throw new ArgumentException($"{Path} is not found in filesystem");
            }

            throw new ArgumentException($"{Path} is not found in filesystem");
        }

        public bool HasNode(string Path)
        {
            try
            {
                var Node = Get(Path);
                return Node != null;
            }
            catch(Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Resolve a node by path ("/", "/soc/uart@0") or by label ("&uart0" via /__symbols__).
        /// Returns null when the node is not found.
        /// </summary>
        public Types.Node Resolve(string Path)
        {
            if (string.IsNullOrEmpty(Path) || (Path == "/") || (Path == "."))
                return Root;

            var P = Path.Trim();
            if (P.Length == 0)
                return Root;

            if (P[0] == '&')
            {
                var Symbols = Root.GetNode("__symbols__");
                if (Symbols == null)
                    return null;

                var Target = Symbols.GetStringValue(P.Substring(1));
                return (Target != null) ? Resolve(Target) : null;
            }

            var Parts = P.Split('/');
            Types.Node Current = Root;
            foreach (var Part in Parts)
            {
                if ((Part.Length == 0) || (Part == "."))
                    continue;

                Current = Current.GetNode(Part);
                if (Current == null)
                    return null;
            }

            return Current;
        }

        /// <summary>
        /// Resolve the parent of a literal path. Root is returned for top-level names.
        /// <paramref name="Name"/> receives the final path component.
        /// </summary>
        public Types.Node ResolveParent(string Path, out string Name)
        {
            Name = null;
            if (string.IsNullOrEmpty(Path))
                return null;

            var P = Path.Trim().TrimEnd('/');
            if (P.Length == 0)
                return null;

            var Slash = P.LastIndexOf('/');
            if (Slash < 0)
            {
                Name = P;
                return Root;
            }

            Name = P.Substring(Slash + 1);
            if (Name.Length == 0)
                return null;

            var ParentPath = P.Substring(0, Slash);
            return Resolve(ParentPath.Length == 0 ? "/" : ParentPath);
        }
    }
}
