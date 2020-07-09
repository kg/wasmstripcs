﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wasm.Model;

namespace WasmStrip {
    class Config {
        public string ModulePath;

        public string ReportPath;

        public string GraphPath;
        public List<Regex> GraphRegexes = new List<Regex>();

        public string DiffAgainst;
        public string DiffPath;

        public string StripOutputPath;
        public string StripRetainListPath;
        public string StripListPath;
        public List<Regex> StripRetainRegexes = new List<Regex>();
        public List<Regex> StripRegexes = new List<Regex>();

        public string DumpSectionsPath;
        public List<string> DumpSectionRegexes = new List<string>();
    }

    class NamespaceInfo {
        public int Index;
        public string Name;
        public uint FunctionCount;
        public uint SizeBytes;
        public HashSet<NamespaceInfo> ChildNamespaces = new HashSet<NamespaceInfo>();
    }

    class FunctionInfo {
        public uint Index;
        public uint TypeIndex;
        public func_type Type;
        public function_body Body;
        public string Name;
        public int LocalsSize;
        public int NumLocals;
    }

    class Program {
        public static int Main (string[] _args) {
            var execStarted = false;

            try {
                var args = new List<string> (_args);

                var config = new Config();
                var argErrorCount = 0;

                for (int i = 0; i < args.Count; i++) {
                    var arg = args[i];
                    if (arg[0] == '@') {
                        try {
                            ParseResponseFile(arg.Substring(1), args);
                        } catch (Exception exc) {
                            Console.Error.WriteLine($"Error parsing response file '{arg}': {exc}");
                            argErrorCount++;
                        }
                        args.RemoveAt(i--);
                    } else if (arg.StartsWith("--")) {
                        if (arg == "--")
                            break;

                        ParseOption(arg.Substring(2), config);
                        args.RemoveAt(i--);
                    } else {
                        if (arg.StartsWith('"') && arg.EndsWith('"')) {
                            arg = arg.Substring(1, arg.Length - 2);
                            args[i] = arg;
                        }

                        try {
                            if (!File.Exists(arg))
                                throw new FileNotFoundException(arg);
                        } catch (Exception exc) {
                            Console.Error.WriteLine($"Argument error for '{arg}': {exc}");
                            argErrorCount++;
                        }
                    }
                }

                if (argErrorCount > 0)
                    return 2;

                if (args.Count != 1)
                    return 1;

                config.ModulePath = args[0];
                if (string.IsNullOrWhiteSpace(config.ModulePath) || !File.Exists(config.ModulePath)) {
                    Console.Error.WriteLine($"File not found: '{config.ModulePath}'");
                    return 3;
                }

                execStarted = true;

                Console.WriteLine($"Processing module {config.ModulePath}...");

                var wasmBytes = File.ReadAllBytes(config.ModulePath);
                var wasmStream = new BinaryReader(new MemoryStream(wasmBytes), System.Text.Encoding.UTF8, false);
                var functions = new Dictionary<uint, FunctionInfo>();
                WasmReader wasmReader;
                using (wasmStream) {
                    wasmReader = new WasmReader(wasmStream);
                    wasmReader.FunctionBodyCallback = (fb, br) => {
                        var info = ProcessFunctionBody(wasmReader, fb, br, config);
                        functions[info.Index] = info;
                    };

                    Console.Write("Reading module...");
                    wasmReader.Read();

                    foreach (var kvp in wasmReader.FunctionNames) {
                        var biasedIndex = (kvp.Key - (int)wasmReader.ImportedFunctionCount);
                        if (biasedIndex < 0)
                            continue;

                        functions[(uint)biasedIndex].Name = kvp.Value;
                    }

                    ClearLine("Analyzing module.");

                    AnalysisData data = null;
                    if ((config.ReportPath != null) || (config.GraphPath != null))
                        data = new AnalysisData(config, wasmStream, wasmReader, functions);

                    ClearLine("Generating reports...");

                    if (config.ReportPath != null)
                        GenerateReport(config, wasmStream, wasmReader, data);

                    if (config.GraphPath != null)
                        GenerateGraph(config, wasmStream, wasmReader, data);

                    ClearLine("Dumping raw data...");

                    if (config.DumpSectionsPath != null)
                        DumpSections(config, wasmStream, wasmReader);

                    ClearLine("Stripping methods...");
                    if (config.StripOutputPath != null)
                        GenerateStrippedModule(config, wasmStream, wasmReader);

                    ClearLine("OK.");
                    Console.WriteLine();
                }
            } finally {
                if (!execStarted) {
                    Console.Error.WriteLine("Usage: WasmStrip module.wasm [--option ...] [@response.rsp]");
                    Console.Error.WriteLine("  --report-out=filename.xml");
                    Console.Error.WriteLine("  --graph-out=filename.dot");
                    Console.Error.WriteLine("    --graph-filter=regex [...]");
                    Console.Error.WriteLine("  --diff-against=oldmodule.wasm --diff-out=filename.csv");
                    Console.Error.WriteLine("  --dump-sections[=regex] --dump-sections-to=outdir/");
                    Console.Error.WriteLine("  --strip-out=newmodule.wasm");
                    Console.Error.WriteLine("    --strip-section=regex [...]");
                    Console.Error.WriteLine("    --strip=regex [...]");
                    Console.Error.WriteLine("    --strip-list=regexes.txt");
                    Console.Error.WriteLine("    --retain=regex [...]");
                    Console.Error.WriteLine("    --retain-list=regexes.txt");
                }

                if (Debugger.IsAttached) {
                    Console.WriteLine("Press enter to exit");
                    Console.ReadLine();
                }
            }

            return 0;
        }

        private static void ClearLine (string newText = null) {
            Console.CursorLeft = 0;
            Console.Write(new string(' ', 50));
            Console.CursorLeft = 0;
            if (newText != null)
                Console.Write(newText);
        }

        private static void GenerateStrippedModule (Config config, BinaryReader wasmStream, WasmReader wasmReader) {
            throw new NotImplementedException();
        }

        private static void EnsureValidPath (string filename) {
            var directoryName = Path.GetDirectoryName(filename);
            Directory.CreateDirectory(directoryName);
        }

        private static void DumpSections (Config config, BinaryReader wasmStream, WasmReader wasmReader) {
            Directory.CreateDirectory(config.DumpSectionsPath);

            for (int i = 0; i < wasmReader.SectionHeaders.Count; i++) {
                var sh = wasmReader.SectionHeaders[i];
                var id = $"{i:00} {sh.id.ToString()} {sh.name ?? ""}".Trim();
                var path = Path.Combine(config.DumpSectionsPath, id);

                if (config.DumpSectionRegexes.Count > 0) {
                    if (!config.DumpSectionRegexes.Any(re => Regex.IsMatch(id, re)))
                        continue;
                }

                using (var outStream = File.OpenWrite(path)) {
                    outStream.SetLength(0);
                    var sw = new ModuleSaw.StreamWindow(wasmStream.BaseStream, sh.StreamPayloadStart, sh.StreamPayloadEnd - sh.StreamPayloadStart);
                    sw.CopyTo(outStream);
                }
            }
        }

        private class AnalysisData {
            public readonly Dictionary<uint, FunctionInfo> Functions;
            public readonly Dictionary<string, NamespaceInfo> Namespaces;
            public readonly Dictionary<FunctionInfo, FunctionInfo[]> DirectDependencies;
            public readonly DependencyGraphNode[] DependencyGraph;

            public AnalysisData (Config config, BinaryReader wasmStream, WasmReader wasmReader, Dictionary<uint, FunctionInfo> functions) {
                Functions = functions;
                Namespaces = ComputeNamespaceSizes(this);
                Console.Write(".");
                DirectDependencies = ComputeDirectDependencies(config, wasmStream, wasmReader, this);
                Console.Write(".");
                DependencyGraph = ComputeDependencyGraph(config, this);
            }
        }

        private static void GenerateGraph (
            Config config, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data
        ) {
            EnsureValidPath(config.GraphPath);

            using (var output = new StreamWriter(config.GraphPath, false, Encoding.UTF8)) {
                output.WriteLine("digraph namespaces {");

                const int maxLength = 24;
                const int minCount = 2;

                var namespaceDependencies = data.DependencyGraph.Where(dgn => dgn.NamespaceName != null).ToLookup(dgn => dgn.NamespaceName);

                var referencedNamespaces = new HashSet<NamespaceInfo>();
                foreach (var kvp in data.Namespaces) {
                    if ((config.GraphRegexes.Count > 0) && !config.GraphRegexes.Any(gr => gr.IsMatch(kvp.Key)))
                        continue;

                    if (kvp.Value.FunctionCount < minCount)
                        continue;

                    foreach (var cns in kvp.Value.ChildNamespaces)
                        referencedNamespaces.Add(cns);

                    if (namespaceDependencies.Contains(kvp.Key)) {
                        var dgn = namespaceDependencies[kvp.Key].First();

                        foreach (var cn in dgn.ReferencedNamespaces)
                            referencedNamespaces.Add(data.Namespaces[cn.NamespaceName]);
                    }
                }

                var namespaces = new HashSet<NamespaceInfo>();
                foreach (var kvp in data.Namespaces) {
                    namespaces.Add(kvp.Value);

                    if (namespaceDependencies.Contains(kvp.Key)) {
                        var dgn = namespaceDependencies[kvp.Key].First();

                        foreach (var rn in dgn.ReferencedNamespaces)
                            namespaces.Add(data.Namespaces[rn.NamespaceName]);
                    }
                }

                var labelsNeeded = new HashSet<NamespaceInfo>();

                foreach (var nsi in namespaces) {
                    if (!referencedNamespaces.Contains(nsi)) {
                        if (nsi.FunctionCount < minCount)
                            continue;

                        if ((config.GraphRegexes.Count > 0) && !config.GraphRegexes.Any(gr => gr.IsMatch(nsi.Name)))
                            continue;
                    }

                    labelsNeeded.Add(nsi);

                    foreach (var cns in nsi.ChildNamespaces) {
                        if (cns.FunctionCount < minCount)
                            continue;

                        output.WriteLine($"\t\"ns{nsi.Index.ToString()}\" -> \"ns{cns.Index.ToString()}\";");

                        labelsNeeded.Add(nsi);
                        labelsNeeded.Add(cns);
                    }

                    if (namespaceDependencies.Contains(nsi.Name)) {
                        var dgn = namespaceDependencies[nsi.Name].First();

                        foreach (var rn in dgn.ReferencedNamespaces) {
                            var rni = data.Namespaces[rn.NamespaceName];
                            labelsNeeded.Add(rni);
                            output.WriteLine($"\t\"ns{nsi.Index.ToString()}\" -> \"ns{rni.Index.ToString()}\";");
                        }
                    }
                }

                foreach (var nsi in labelsNeeded) {
                    var label = nsi.Name.Replace("*", "");
                    if (label.Length > maxLength)
                        label = label.Substring(0, maxLength) + "...";

                    var color = config.GraphRegexes.Any(gr => gr.IsMatch(nsi.Name)) ? "AAAAAA" : "DFDFDF";
                    output.WriteLine($"\t\"ns{nsi.Index.ToString()}\" [label=\"{label}\", style=\"filled\", color=\"#{color}\"];");
                }

                output.WriteLine("}");
            }
        }

        private static void GenerateReport (
            Config config, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data
        ) {
            EnsureValidPath(config.ReportPath);

            using (var output = new StreamWriter(config.ReportPath, false, Encoding.UTF8)) {
                output.WriteLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>
<?mso-application progid=""Excel.Sheet""?>
<Workbook xmlns=""urn:schemas-microsoft-com:office:spreadsheet"" xmlns:x=""urn:schemas-microsoft-com:office:excel"" xmlns:ss=""urn:schemas-microsoft-com:office:spreadsheet"" xmlns:html=""https://www.w3.org/TR/html401/"">"
                );

                WriteSheetHeader(
                    output,
                    "Sizes",
                    new[] { 70, 60, 350, 70, 100 },
                    new[] { "Type", "Index", "Name", "Size (Bytes)", "Signature" }
                );

                var i = 0;
                foreach (var sh in wasmReader.SectionHeaders) {
                    output.WriteLine("            <Row>");
                    WriteCell(output, "String", "section");
                    WriteCell(output, "Number", i.ToString());
                    WriteCell(output, "String", $"{sh.id.ToString()}{(string.IsNullOrWhiteSpace(sh.name) ? "" : " '" + sh.name + "'")}");
                    WriteCell(output, "Number", sh.payload_len.ToString());
                    output.WriteLine("            </Row>");
                    i++;
                }

                foreach (var ns in data.Namespaces.Values) {
                    if (ns.FunctionCount < 2)
                        continue;

                    output.WriteLine("            <Row>");
                    WriteCell(output, "String", "namespace");
                    WriteCell(output, "Number", ns.Index.ToString());
                    WriteCell(output, "String", ns.Name);
                    WriteCell(output, "Number", ns.SizeBytes.ToString());
                    WriteCell(output, "String", $"{ns.FunctionCount:00000} function(s)");
                    output.WriteLine("            </Row>");
                }

                foreach (var fn in data.Functions.Values) {
                    output.WriteLine("            <Row>");
                    WriteCell(output, "String", "function");
                    WriteCell(output, "Number", fn.Index.ToString());
                    WriteCell(output, "String", fn.Name ?? "");
                    WriteCell(output, "Number", fn.Body.body_size.ToString());
                    WriteCell(output, "String", GetSignatureForType(fn.Type));
                    output.WriteLine("            </Row>");
                }

                WriteSheetFooter(output, 5);

                WriteSheetHeader(
                    output, "Dependencies",
                    new[] { 80, 500, 50, 50, 60, 70, 70, 80 },
                    new[] { "Type", "Name", "In", "Out", "Out (NS)", "Size", "Out (Deep)", "Size (Deep)" }
                );

                foreach (var entry in data.DependencyGraph) {
                    output.WriteLine("            <Row>");
                    WriteCell(output, "String", entry.NamespaceName != null ? "namespace" : "function");
                    WriteCell(output, "String", entry.NamespaceName ?? (entry.Function.Name ?? $"#{entry.Function.Index}"));
                    WriteCell(output, "Number", entry.Dependents.ToString());
                    WriteCell(output, "Number", entry.DirectDependencyCount.ToString());
                    WriteCell(output, "Number", entry.ReferencedNamespaces != null ? entry.ReferencedNamespaces.Count.ToString() : "");
                    WriteCell(output, "Number", entry.ShallowSize.ToString());
                    WriteCell(output, "Number", entry.DeepDependencies.ToString());
                    WriteCell(output, "Number", entry.DeepSize.ToString());
                    output.WriteLine("            </Row>");
                }

                WriteSheetFooter(output, 8);

                output.WriteLine("</Workbook>");
            }
        }

        class DependencyGraphNode {
            public FunctionInfo Function;
            public string NamespaceName;

            public uint ShallowSize;
            public uint DeepSize;

            public DependencyGraphNode[] DirectDependencies;
            public int DeepDependencies;

            public int Recursions;
            public int Dependents;

            public DependencyGraphNode ParentNamespace;
            public HashSet<DependencyGraphNode> ChildFunctions;
            public HashSet<DependencyGraphNode> ReferencedNamespaces;

            public int DirectDependencyCount {
                get {
                    if (Function != null)
                        return DirectDependencies?.Length ?? 0;

                    var hs = new HashSet<DependencyGraphNode>();
                    foreach (var cf in ChildFunctions) {
                        if (cf.DirectDependencies == null)
                            continue;

                        foreach (var dd in cf.DirectDependencies)
                            hs.Add(dd);
                    }
                    return hs.Count;
                }
            }
        }

        private static DependencyGraphNode[] ComputeDependencyGraph (
            Config config, AnalysisData data
        ) {
            var namespaceNodes = new Dictionary<string, DependencyGraphNode>();
            var functionNodes = new Dictionary<FunctionInfo, DependencyGraphNode>();

            foreach (var fn in data.Functions.Values) {
                var fnode = new DependencyGraphNode {
                    Function = fn,
                    ShallowSize = fn.Body.body_size
                };

                string namespaceName = fn.Name;
                if ((namespaceName = GetNamespaceName(namespaceName)) != null) {
                    DependencyGraphNode namespaceNode;
                    if (!namespaceNodes.TryGetValue(namespaceName, out namespaceNode))
                        namespaceNode = namespaceNodes[namespaceName] = new DependencyGraphNode {
                            NamespaceName = namespaceName,
                            ChildFunctions = new HashSet<DependencyGraphNode>(),
                            ReferencedNamespaces = new HashSet<DependencyGraphNode>()
                        };

                    namespaceNode.ShallowSize += fnode.ShallowSize;
                    namespaceNode.ChildFunctions.Add(fnode);
                    fnode.ParentNamespace = namespaceNode;
                }

                functionNodes[fn] = fnode;
            }

            foreach (var kvp in functionNodes) {
                FunctionInfo[] dd;
                if (!data.DirectDependencies.TryGetValue(kvp.Key, out dd))
                    continue;

                kvp.Value.DirectDependencies = (from fi in dd select functionNodes[fi]).ToArray();

                foreach (var dep in kvp.Value.DirectDependencies) {
                    if (dep.Function == kvp.Key) {
                        kvp.Value.Recursions += 1;
                        continue;
                    }

                    if ((kvp.Value.ParentNamespace != null) && (dep.ParentNamespace != null))
                        kvp.Value.ParentNamespace.ReferencedNamespaces.Add(dep.ParentNamespace);

                    // Propagate dependencies upward
                    var upward = functionNodes[dep.Function];
                    upward.Dependents += 1;

                    if (upward.ParentNamespace != null)
                        upward.ParentNamespace.Dependents += 1;
                }
            }

            foreach (var kvp in functionNodes)
                ComputeDeepDependencies(config, data, functionNodes, namespaceNodes, kvp.Value);

            foreach (var kvp in namespaceNodes)
                ComputeDeepDependencies(config, data, functionNodes, namespaceNodes, kvp.Value);

            return (from nsn in namespaceNodes.Values where nsn.ChildFunctions.Count > 1 select nsn).Concat(functionNodes.Values).ToArray();
        }

        private static void ComputeDeepDependencies (
            Config config, AnalysisData data,
            Dictionary<FunctionInfo, DependencyGraphNode> functionNodes, 
            Dictionary<string, DependencyGraphNode> namespaceNodes,
            DependencyGraphNode node
        ) {
            if (node.Function != null)
                node.DeepSize = node.Function.Body.body_size;
            else
                node.DeepSize = 0;

            var seenList = new HashSet<FunctionInfo>();
            var todoList = new Queue<FunctionInfo>();

            if (node.DirectDependencies != null) {
                foreach (var dep in node.DirectDependencies) {
                    seenList.Add(dep.Function);
                    todoList.Enqueue(dep.Function);
                }
            } else if (node.ChildFunctions != null) {
                foreach (var cf in node.ChildFunctions) {
                    seenList.Add(cf.Function);
                    todoList.Enqueue(cf.Function);
                }
            }

            while (todoList.Count > 0) {
                var dep = todoList.Dequeue();

                node.DeepSize += dep.Body.body_size;
                node.DeepDependencies += 1;

                var depNode = functionNodes[dep];
                if (depNode.DirectDependencies == null)
                    continue;

                foreach (var subDep in depNode.DirectDependencies) {
                    if (!seenList.Contains(subDep.Function)) {
                        seenList.Add(subDep.Function);
                        todoList.Enqueue(subDep.Function);
                    }
                }
            }
        }

        private static Dictionary<FunctionInfo, FunctionInfo[]> ComputeDirectDependencies (
            Config config, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data
        ) {
            var result = new Dictionary<FunctionInfo, FunctionInfo[]>();
            var temp = new HashSet<FunctionInfo>();

            foreach (var fn in data.Functions.Values) {
                temp.Clear();
                GatherDirectDependencies(config, wasmStream, wasmReader, data, fn, temp);

                if (temp.Count > 0)
                    result[fn] = temp.ToArray();
            }

            return result;
        }

        private static void GatherDirectDependencies (
            Config config, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data, 
            FunctionInfo function, HashSet<FunctionInfo> dependencies
        ) {
            using (var subStream = new ModuleSaw.StreamWindow(
                function.Body.Stream, function.Body.StreamOffset, function.Body.StreamEnd - function.Body.StreamOffset)
            ) {
                var reader = new ExpressionReader(new BinaryReader(subStream));

                Expression expr;
                while (reader.TryReadExpression(out expr)) {
                    if (!reader.TryReadExpressionBody(ref expr))
                        throw new Exception($"Failed to read body of {expr.Opcode}");

                    GatherDirectDependencies(expr, wasmReader, data, dependencies);
                }
            }
        }

        private static void GatherDirectDependencies (Expression expr, WasmReader wasmReader, AnalysisData data, HashSet<FunctionInfo> dependencies) {
            if (expr.Opcode == Opcodes.call) {
                var index = expr.Body.U.u32;
                if (index < wasmReader.ImportedFunctionCount) {
                    // Calling an import
                } else {
                    index -= wasmReader.ImportedFunctionCount;
                    FunctionInfo callee;
                    if (!data.Functions.TryGetValue(index, out callee))
                        throw new Exception($"Invalid call target: {index}");
                    else
                        dependencies.Add(callee);
                }
            } else if ((expr.Body.children != null) && (expr.Body.children.Count > 0)) {
                foreach (var child in expr.Body.children)
                    GatherDirectDependencies(child, wasmReader, data, dependencies);
            }
        }

        private static Dictionary<string, NamespaceInfo> ComputeNamespaceSizes (AnalysisData data) {
            var namespaces = new Dictionary<string, NamespaceInfo>();
            int i = 0;
            foreach (var fn in data.Functions.Values) {
                if (string.IsNullOrWhiteSpace(fn.Name))
                    continue;

                var namespaceName = fn.Name;
                NamespaceInfo previousNamespace = null;

                while ((namespaceName = GetNamespaceName(namespaceName)) != null) {
                    NamespaceInfo ns;
                    if (!namespaces.TryGetValue(namespaceName, out ns)) {
                        namespaces[namespaceName] = ns = new NamespaceInfo { Name = namespaceName, Index = i++ };
                    }

                    if (previousNamespace != null)
                        ns.ChildNamespaces.Add(previousNamespace);

                    ns.FunctionCount += 1;
                    ns.SizeBytes += fn.Body.body_size;
                    previousNamespace = ns;
                }
            }

            return namespaces;
        }

        private static void WriteSheetHeader (StreamWriter output, string sheetName, int[] widths, string[] labels) {
            if (widths.Length != labels.Length)
                throw new ArgumentException();

            output.WriteLine($"<Worksheet ss:Name=\"{sheetName}\">");
            output.WriteLine($"<Names><NamedRange ss:Name=\"_FilterDatabase\" ss:RefersTo=\"='{sheetName}'!R1C1:R1C{widths.Length}\" ss:Hidden=\"1\"/></Names>");
            output.WriteLine($"<Table>");

            for (var i = 0; i < widths.Length; i++)
                output.WriteLine($"<Column ss:Index=\"{i + 1}\" ss:Width=\"{widths[i]}\" />");

            output.WriteLine("<Row>");
            for (var i = 0; i < labels.Length; i++)
                output.WriteLine($"<Cell><Data ss:Type=\"String\">{labels[i]}</Data><NamedCell ss:Name=\"_FilterDatabase\"/></Cell>");

            output.WriteLine("</Row>");
        }

        private static void WriteSheetFooter (StreamWriter output, int columnCount) {
            output.WriteLine(@"        </Table>
        <WorksheetOptions xmlns=""urn:schemas-microsoft-com:office:excel"">
            <FreezePanes/>
            <FrozenNoSplit/>
            <SplitHorizontal>1</SplitHorizontal>
            <TopRowBottomPane>1</TopRowBottomPane>
            <ActivePane>2</ActivePane>
            <Panes>
                <Pane>
                    <Number>3</Number>
                </Pane>
                <Pane>
                    <Number>2</Number>
                </Pane>
            </Panes>
        </WorksheetOptions>");
            output.WriteLine($"<AutoFilter x:Range=\"R1C1:R1C{columnCount}\" xmlns=\"urn:schemas-microsoft-com:office:excel\"></AutoFilter>");
            output.WriteLine("</Worksheet>");
        }

        private static void WriteCell (StreamWriter sw, string type, string value) {
            var escaped = System.Security.SecurityElement.Escape(value);
            sw.WriteLine($"                <Cell><Data ss:Type=\"{type}\">{escaped}</Data></Cell>");
        }

        private static string GetNamespaceName (string name) {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var firstParen = name.IndexOf("(");

            // C++ names will have 'type ns::fn' format so strip the leading type if present
            if (name.Contains("::") && firstParen > 0) {
                var firstSpace = name.IndexOfAny(new[] { ' ', '<', '(' });
                if ((firstSpace > 0) && (name[firstSpace] == ' ')) {
                    name = name.Substring(firstSpace + 1);
                    firstParen = name.IndexOf("(");
                }
            }

            // If there's an opening parentheses (function name) stop searching there
            if (firstParen < 0)
                firstParen = name.Length;

            string result;
            var lastNsBreak = name.LastIndexOf("::", Math.Min(firstParen, Math.Max(0, name.Length - 4)), StringComparison.Ordinal);
            if (lastNsBreak <= 0) {
                var lastUnderscore = name.LastIndexOf("_", Math.Min(firstParen, Math.Max(0, name.Length - 3)), StringComparison.Ordinal);
                if (lastUnderscore > 0)
                    result = name.Substring(0, lastUnderscore + 1) + "*";
                else
                    result = null;
            } else {
                result = name.Substring(0, lastNsBreak + 2) + "*";
            }

            if (string.IsNullOrWhiteSpace(result) || (result.Trim() == "::"))
                return null;
            else
                return result;
        }

        private static string GetSignatureForType (func_type type) {
            var sb = new StringBuilder();

            Append(sb, type.return_type);
            sb.Append("(");
            foreach (var pt in type.param_types)
                Append(sb, pt);
            sb.Append(")");

            return sb.ToString();
        }

        private static void Append (StringBuilder sb, LanguageTypes type) {
            switch (type) {
                case LanguageTypes.none:
                    return;
                case LanguageTypes.i32:
                    sb.Append("i");
                    return;
                case LanguageTypes.i64:
                    sb.Append("l");
                    return;
                case LanguageTypes.f32:
                    sb.Append("s");
                    return;
                case LanguageTypes.f64:
                    sb.Append("d");
                    return;
                case LanguageTypes.anyfunc:
                case LanguageTypes.func:
                    sb.Append("f");
                    return;
                default:
                    // FIXME
                    sb.Append("?");
                    return;
            }
        }

        public static FunctionInfo ProcessFunctionBody (WasmReader wr, function_body fb, BinaryReader br, Config config) {
            var localsSize = 0;
            var numLocals = 0;
            uint typeIndex;

            var result = new FunctionInfo {
                Index = fb.Index,
                TypeIndex = (typeIndex = wr.Functions.types[fb.Index]),
                Type = wr.Types.entries[typeIndex],
                Body = fb
            };

            foreach (var param in result.Type.param_types)
                localsSize += GetSizeForLanguageType(param);
            foreach (var local in fb.locals) {
                localsSize += (int)(GetSizeForLanguageType(local.type) * local.count);
                numLocals += (int)local.count;
            }

            result.LocalsSize = localsSize;
            result.NumLocals = numLocals;

            return result;
        }

        private static int GetSizeForLanguageType (LanguageTypes type) {
            switch (type) {
                case LanguageTypes.f32:
                    return 4;
                case LanguageTypes.f64:
                    return 8;
                case LanguageTypes.i32:
                    return 4;
                case LanguageTypes.i64:
                    return 8;
                case LanguageTypes.anyfunc:
                case LanguageTypes.func:
                    return 4;
                default:
                    return 0;
            }
        }

        private static Expression MakeI32Const (int i) {
            return new Expression {
                Opcode = Opcodes.i32_const,
                Body = { U = { i32 = i }, Type = ExpressionBody.Types.i32 }
            };
        }

        public static void ParseOption (string arg, Config config) {
            string operand = null;

            var equalsOffset = arg.IndexOfAny(new[] { ':', '=' });
            if (equalsOffset >= 0) {
                operand = arg.Substring(equalsOffset + 1);
                arg = arg.Substring(0, equalsOffset);
            }

            operand = operand.Replace("\\\"", "\"");

            if (operand.StartsWith('"') && operand.EndsWith('"'))
                operand = operand.Substring(1, operand.Length - 2);

            arg = arg.Replace("-", "");

            switch (arg.ToLower()) {
                case "report":
                case "reportout":
                case "reportoutput":
                case "reportpath":
                    config.ReportPath = operand;
                    break;
                case "graph":
                case "graphout":
                case "graphoutput":
                case "graphpath":
                    config.GraphPath = operand;
                    break;
                case "graphregex":
                case "graphfilter":
                    config.GraphRegexes.Add(new Regex(operand));
                    break;
                case "diff":
                case "diffagainst":
                    config.DiffAgainst = operand;
                    break;
                case "diffout":
                case "diffoutput":
                case "diffpath":
                    config.DiffPath = operand;
                    break;
                case "output":
                case "outpath":
                case "out":
                case "stripoutput":
                case "stripoutpath":
                case "stripout":
                    config.StripOutputPath = operand;
                    break;
                case "retain":
                case "retainRegex":
                    config.StripRetainRegexes.Add(new Regex(operand));
                    break;
                case "strip":
                case "stripRegex":
                    config.StripRegexes.Add(new Regex(operand));
                    break;
                case "striplist":
                    TrySet(ref config.StripListPath, operand);
                    break;
                case "retainlist":
                    TrySet(ref config.StripRetainListPath, operand);
                    break;
                case "dumpsections":
                    if (string.IsNullOrWhiteSpace(operand))
                        config.DumpSectionRegexes.Add(".*");
                    else
                        config.DumpSectionRegexes.Add(operand);
                    break;
                case "dumpsectionsout":
                case "dumpsectionsoutput":
                case "dumpsectionsto":
                case "dumpsectionspath":
                    config.DumpSectionsPath = operand;
                    break;
            }
        }

        public static void TrySet (ref string result, string value) {
            if (!string.IsNullOrWhiteSpace(result))
                throw new Exception ($"Argument was already set to '{result}' when trying to set it to '{value}'");

            result = value;
        }

        public static void ParseResponseFile (string path, List<string> args) {
            if (path.StartsWith('"') && path.EndsWith('"'))
                path = path.Substring(1, path.Length - 2);

            foreach (var line in File.ReadAllLines(path))
                args.Add(line);
        }

        public static void Assert (
            bool b,
            string description = null,
            [CallerMemberName] string memberName = "",
            [CallerFilePath]   string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0
        ) {
            if (!b)
                throw new Exception(string.Format(
                    "{0} failed in {1} @ {2}:{3}",
                    description ?? "Assert",
                    memberName, Path.GetFileName(sourceFilePath), sourceLineNumber
                ));
        }

        public static string GetPathOfAssembly (Assembly assembly) {
            var uri = new Uri(assembly.CodeBase);
            var result = Uri.UnescapeDataString(uri.AbsolutePath);

            if (String.IsNullOrWhiteSpace(result))
                result = assembly.Location;

            result = result.Replace('/', System.IO.Path.DirectorySeparatorChar);

            return result;
        }
    }
}
