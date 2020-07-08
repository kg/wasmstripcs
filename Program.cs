using System;
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
        public string DiffAgainst;
        public string DiffPath;

        public string StripOutputPath;
        public string StripRetainListPath;
        public string StripListPath;
        public List<string> StripRetainRegexes = new List<string>();
        public List<string> StripRegexes = new List<string>();

        public string DumpSectionsPath;
        public List<string> DumpSectionRegexes = new List<string>();
    }

    class NamespaceInfo {
        public string Name;
        public uint FunctionCount;
        public uint SizeBytes;
    }

    class FunctionInfo {
        public uint Index;
        public uint TypeIndex;
        public func_type Type;
        public function_body Body;
        public string Name;
        public List<string> ExportNames;
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
                    wasmReader.Read();

                    foreach (var kvp in wasmReader.FunctionNames) {
                        var biasedIndex = (kvp.Key - wasmReader.ImportedFunctionCount);
                        if (biasedIndex < 0)
                            continue;

                        functions[(uint)biasedIndex].Name = kvp.Value;
                    }

                    if (config.ReportPath != null)
                        GenerateReport(config, wasmStream, wasmReader, functions);

                    if (config.DumpSectionsPath != null)
                        DumpSections(config, wasmStream, wasmReader);
                }
            } finally {
                if (!execStarted) {
                    Console.Error.WriteLine("Usage: WasmStrip module.wasm [--option ...] [@response.rsp]");
                    Console.Error.WriteLine("  --report-out=filename.xml");
                    Console.Error.WriteLine("  --diff-against=oldmodule.wasm --diff-out=filename.csv");
                    Console.Error.WriteLine("  --dump-sections[=regex] --dump-sections-to=outdir/");
                    /*
                    Console.Error.WriteLine("  --out=newmodule.wasm");
                    Console.Error.WriteLine("    --strip-section=regex [...]");
                    Console.Error.WriteLine("    --strip=regex [...]");
                    Console.Error.WriteLine("    --strip-list=regexes.txt");
                    Console.Error.WriteLine("    --retain=regex [...]");
                    Console.Error.WriteLine("    --retain-list=regexes.txt");
                    */
                }

                if (Debugger.IsAttached) {
                    Console.WriteLine("Press enter to exit");
                    Console.ReadLine();
                }
            }

            return 0;
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

        private static void GenerateReport (Config config, BinaryReader wasmStream, WasmReader wasmReader, Dictionary<uint, FunctionInfo> functions) {
            var lastFolder = Path.GetFileName(Path.GetDirectoryName(config.ModulePath));
            var sheetName = Path.Combine(lastFolder, Path.GetFileName(config.ModulePath)).Replace('\\', '|').Replace('/', '|');

            using (var output = new StreamWriter(config.ReportPath, false, Encoding.UTF8)) {
                output.WriteLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>
<?mso-application progid=""Excel.Sheet""?>
<Workbook xmlns=""urn:schemas-microsoft-com:office:spreadsheet"" xmlns:x=""urn:schemas-microsoft-com:office:excel"" xmlns:ss=""urn:schemas-microsoft-com:office:spreadsheet"" xmlns:html=""https://www.w3.org/TR/html401/"">"
                );

                output.WriteLine($"<Worksheet ss:Name=\"{sheetName}\">");
                output.WriteLine($"<Names><NamedRange ss:Name=\"_FilterDatabase\" ss:RefersTo=\"='{sheetName}'!R1C1:R1C5\" ss:Hidden=\"1\"/></Names>");
                output.WriteLine($"<Table>");

                WriteColumns(
                    output,
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

                var namespaces = new Dictionary<string, NamespaceInfo>();
                foreach (var fn in functions.Values) {
                    if (string.IsNullOrWhiteSpace(fn.Name))
                        continue;

                    var namespaceName = fn.Name;

                    while ((namespaceName = GetNamespaceName(namespaceName)) != null) {
                        NamespaceInfo ns;
                        if (!namespaces.TryGetValue(namespaceName, out ns))
                            namespaces[namespaceName] = ns = new NamespaceInfo { Name = namespaceName };

                        ns.FunctionCount += 1;
                        ns.SizeBytes += fn.Body.body_size;
                    }
                }

                i = 0;
                foreach (var ns in namespaces.Values) {
                    if (ns.FunctionCount < 2)
                        continue;

                    output.WriteLine("            <Row>");
                    WriteCell(output, "String", "namespace");
                    WriteCell(output, "Number", i.ToString());
                    WriteCell(output, "String", ns.Name);
                    WriteCell(output, "Number", ns.SizeBytes.ToString());
                    WriteCell(output, "String", $"{ns.FunctionCount:00000} function(s)");
                    output.WriteLine("            </Row>");
                    i++;
                }

                foreach (var fn in functions.Values) {
                    output.WriteLine("            <Row>");
                    WriteCell(output, "String", "function");
                    WriteCell(output, "Number", fn.Index.ToString());
                    WriteCell(output, "String", fn.Name ?? "");
                    WriteCell(output, "Number", fn.Body.body_size.ToString());
                    WriteCell(output, "String", GetSignatureForType(fn.Type));
                    output.WriteLine("            </Row>");
                }

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
        </WorksheetOptions>
        <AutoFilter x:Range=""R1C1:R1C5"" xmlns=""urn:schemas-microsoft-com:office:excel""></AutoFilter>
    </Worksheet>
</Workbook>");

            }
        }

        private static void WriteColumns (StreamWriter output, int[] widths, string[] labels) {
            for (var i = 0; i < widths.Length; i++)
                output.WriteLine($"<Column ss:Index=\"{i + 1}\" ss:Width=\"{widths[i]}\" />");

            output.WriteLine("<Row>");
            for (var i = 0; i < labels.Length; i++)
                output.WriteLine($"<Cell><Data ss:Type=\"String\">{labels[i]}</Data><NamedCell ss:Name=\"_FilterDatabase\"/></Cell>");

            output.WriteLine("</Row>");
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

            var lastNsBreak = name.LastIndexOf("::", Math.Min(firstParen, Math.Max(0, name.Length - 4)), StringComparison.Ordinal);
            if (lastNsBreak <= 0) {
                var lastUnderscore = name.LastIndexOf("_", Math.Min(firstParen, Math.Max(0, name.Length - 3)), StringComparison.Ordinal);
                if (lastUnderscore > 0)
                    return name.Substring(0, lastUnderscore + 1) + "*";
                else
                    return null;
            } else {
                return name.Substring(0, lastNsBreak + 2) + "*";
            }
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
                    config.StripOutputPath = operand;
                    break;
                case "retain":
                case "retainRegex":
                    config.StripRetainRegexes.Add(operand);
                    break;
                case "strip":
                case "stripRegex":
                    config.StripRegexes.Add(operand);
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
