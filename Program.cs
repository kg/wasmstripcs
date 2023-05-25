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
using ModuleSaw;
using System.Threading.Tasks;
using System.Threading;

namespace WasmStrip {
    class ReferenceComparer<T> : IEqualityComparer<T> {
        bool IEqualityComparer<T>.Equals (T x, T y) {
            return object.ReferenceEquals(x, y);
        }

        int IEqualityComparer<T>.GetHashCode (T obj) {
            return obj.GetHashCode();
        }
    }

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
        public string StripReportPath;

        public bool VerifyOutput = true;

        public string DumpSectionsPath;
        public List<Regex> DumpSectionRegexes = new List<Regex>();

        public bool DumpFunctions = true, DisassembleFunctions = true;
        public string DumpFunctionsPath;
        public List<Regex> DumpFunctionRegexes = new List<Regex>();
    }

    class NamespaceInfo {
        public static readonly ReferenceComparer<NamespaceInfo> Comparer = new ReferenceComparer<NamespaceInfo>();

        public int Index;
        public string Name;
        public uint FunctionCount;
        public uint SizeBytes;
        public HashSet<NamespaceInfo> ChildNamespaces = new HashSet<NamespaceInfo>(Comparer);
    }

    class FunctionInfoComparer : IEqualityComparer<FunctionInfo> {
        public bool Equals (FunctionInfo x, FunctionInfo y) {
            return (x.Index == y.Index);
        }

        public int GetHashCode (FunctionInfo obj) {
            unchecked { return (int)obj.Index; }
        }
    }

    class FunctionInfo {
        public static readonly FunctionInfoComparer Comparer = new FunctionInfoComparer();

        public uint Index;
        public uint TypeIndex;
        public func_type Type;
        public function_body Body;
        public string Name;
        public int LocalsSize;
        public int NumLocals;
    }

    class Program {
        public static Thread MainThread;

        public static int Main (string[] _args) {
            MainThread = Thread.CurrentThread;

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

                var wasmStream = ReadModule(config.ModulePath, out byte[] wasmBytes);
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

                    AssignFunctionNames(functions, wasmReader);
                    AssignImportNames(wasmReader);

                    ClearLine("Dumping sections...");

                    if (config.DumpSectionsPath != null)
                        DumpSections(config, wasmBytes, wasmReader);

                    ClearLine("Analyzing module.");

                    AnalysisData data = null;
                    if ((config.ReportPath != null) || (config.GraphPath != null)) {
                        var functionArray = new FunctionInfo[functions.Count];
                        functions.Values.CopyTo(functionArray, 0);
                        data = new AnalysisData(config, wasmBytes, wasmStream, wasmReader, functionArray);
                    }

                    ClearLine("Generating reports...");

                    if (config.ReportPath != null) {
                        try {
                            GenerateReport(config, wasmStream, wasmReader, data, config.ReportPath);
                        } catch (Exception exc) {
                            ClearLine();
                            Console.Error.WriteLine("Failed to generate report:{1}{0}", exc, Environment.NewLine);
                            Console.WriteLine();
                        }
                    }

                    if (config.GraphPath != null) {
                        try {
                            GenerateGraph(config, wasmStream, wasmReader, data, config.GraphPath);
                        } catch (Exception exc) {
                            ClearLine();
                            Console.Error.WriteLine("Failed to generate graph:{1}{0}", exc, Environment.NewLine);
                            Console.WriteLine();
                        }
                    }

                    ClearLine("Dumping functions... ");

                    if (config.DumpFunctionsPath != null)
                        DumpFunctions(config, wasmBytes, wasmReader, functions);

                    ClearLine("Stripping methods...");
                    if (config.StripOutputPath != null) {
                        GenerateStrippedModule(config, wasmBytes, wasmStream, wasmReader, functions);

                        var shouldReadOutput = config.VerifyOutput || (config.StripReportPath != null);
                        if (shouldReadOutput) {
                            var resultFunctions = new Dictionary<uint, FunctionInfo>();

                            using (var resultReader = ReadModule(config.StripOutputPath, out _)) {
                                ClearLine("Reading stripped module...");
                                var resultWasmReader = new WasmReader(resultReader);
                                resultWasmReader.FunctionBodyCallback = (fb, br) => {
                                    var info = ProcessFunctionBody(resultWasmReader, fb, br, config);
                                    resultFunctions[info.Index] = info;
                                };
                                resultWasmReader.Read();

                                AssignFunctionNames(resultFunctions, resultWasmReader);

                                if (config.StripReportPath != null) {
                                    ClearLine("Analyzing stripped module.");
                                    var resultArray = new FunctionInfo[resultFunctions.Count];
                                    resultFunctions.Values.CopyTo(resultArray, 0);
                                    var newData = new AnalysisData(config, wasmBytes, resultReader, resultWasmReader, resultArray);

                                    GenerateReport(config, resultReader, resultWasmReader, newData, config.StripReportPath);
                                }
                            }
                        }
                    }

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
                    Console.Error.WriteLine("  --dump-functions=regex --dump-functions-to=outdir/");
                    Console.Error.WriteLine("    --disassemble-only");
                    Console.Error.WriteLine("    --dump-only");
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

        private static void AssignImportNames (WasmReader wasmReader) {
            var imports = wasmReader.Imports.entries;
            for (int i = 0; i < imports.Length; i++) {
                var import = imports[i];
                if (import.kind != external_kind.Function)
                    continue;
                if (wasmReader.FunctionNames.TryGetValue((uint)i, out var s) && !string.IsNullOrWhiteSpace(s))
                    continue;
                wasmReader.FunctionNames[(uint)i] = $"{import.module}.{import.field}";
            }
        }

        private static void AssignFunctionNames (Dictionary<uint, FunctionInfo> functions, WasmReader wasmReader) {
            var offset = wasmReader.FunctionIndexOffset;
            foreach (var kvp in wasmReader.FunctionNames) {
                // FIXME
                if (kvp.Key < offset)
                    continue;

                var biasedIndex = kvp.Key - offset;
                functions[(uint)biasedIndex].Name = kvp.Value;
            }
        }

        private static BinaryReader ReadModule (string path, out byte[] wasmBytes) {
            wasmBytes = File.ReadAllBytes(path);
            var stream = new MemoryStream(wasmBytes, false);
            return new BinaryReader(stream, Encoding.UTF8, false);
        }

        private static void ClearLine (string newText = null) {
            Console.CursorLeft = 0;
            Console.Write(new string(' ', 50));
            Console.CursorLeft = 0;
            if (newText != null)
                Console.Write(newText);
        }

        private static void EnsureValidPath (string filename) {
            var directoryName = Path.GetDirectoryName(filename);
            Directory.CreateDirectory(directoryName);
        }

        private static Stream GetSectionStream (byte[] bytes, SectionHeader header, bool includeHeader) {
            var startOffset = includeHeader ? header.StreamHeaderStart - 1 : header.StreamPayloadStart;
            var size = (int)(header.StreamPayloadEnd - startOffset);
            return new MemoryStream(bytes, (int)startOffset, size, false);
        }

        private static void GenerateStrippedModule (
            Config config, byte[] wasmBytes, BinaryReader wasmStream, WasmReader wasmReader,
            Dictionary<uint, FunctionInfo> functions
        ) {
            EnsureValidPath(config.StripOutputPath);

            using (var o = new BinaryWriter(File.OpenWrite(config.StripOutputPath), Encoding.UTF8, false)) {
                o.BaseStream.SetLength(0);

                o.Write((uint)0x6d736100);
                o.Write((uint)1);

                foreach (var sh in wasmReader.SectionHeaders) {
                    switch (sh.id) {
                        case SectionTypes.Code:
                            using (var sectionScratch = new MemoryStream(1024 * 1024))
                            using (var sectionScratchWriter = new BinaryWriter(sectionScratch, Encoding.UTF8, true)) {
                                GenerateStrippedCodeSection(config, wasmBytes, wasmStream, wasmReader, functions, sh, sectionScratchWriter);
                                sectionScratchWriter.Flush();

                                o.Write((sbyte)sh.id);
                                o.WriteLEB((uint)sectionScratch.Length);
                                o.Flush();

                                sectionScratch.Position = 0;
                                sectionScratch.CopyTo(o.BaseStream);
                            }
                            break;

                        default:
                            using (var ss = GetSectionStream(wasmBytes, sh, true)) {
                                o.Flush();

                                ss.Position = 0;
                                ss.CopyTo(o.BaseStream);
                            }
                            break;
                    }
                }
            }
        }

        private static void GenerateStrippedCodeSection (Config config, byte[] wasmBytes, BinaryReader wasmStream, WasmReader wasmReader, Dictionary<uint, FunctionInfo> functions, SectionHeader sh, BinaryWriter output) {
            output.WriteLEB((uint)wasmReader.Code.bodies.Length);

            var scratchBuffer = new MemoryStream(102400);
            foreach (var body in wasmReader.Code.bodies) {
                scratchBuffer.Position = 0;
                scratchBuffer.SetLength(0);

                using (var scratch = new BinaryWriter(scratchBuffer, Encoding.UTF8, true)) {
                    var fi = functions[body.Index];
                    var name = fi.Name ?? $"#{body.Index}";

                    var retain = config.StripRetainRegexes.Any(r => r.IsMatch(name));
                    var strip = config.StripRegexes.Any(r => r.IsMatch(name));

                    if (strip && !retain) {
                        Console.WriteLine($"// Stripping {name}");
                        GenerateStrippedFunctionBody(body, scratch);
                    } else {
                        CopyExistingFunctionBody(wasmBytes, body, scratch);
                    }

                    scratch.Flush();
                    output.WriteLEB((uint)scratchBuffer.Position);
                    output.Write(scratchBuffer.GetBuffer(), 0, (int)scratchBuffer.Position);
                }
            }
        }

        private static void EmitExpression (
            BinaryWriter writer, ref Expression e
        ) {
            writer.Write((byte)e.Opcode);

            switch (e.Body.Type & ~ExpressionBody.Types.children) {
                case ExpressionBody.Types.none:
                    break;

                case ExpressionBody.Types.u32:
                    writer.WriteLEB(e.Body.U.u32);
                    break;
                case ExpressionBody.Types.u1:
                    writer.Write((byte)e.Body.U.u32);
                    break;
                case ExpressionBody.Types.i64:
                    writer.WriteLEB(e.Body.U.i64);
                    break;
                case ExpressionBody.Types.i32:
                    writer.WriteLEB(e.Body.U.i32);
                    break;
                case ExpressionBody.Types.f64:
                    writer.Write(e.Body.U.f64);
                    break;
                case ExpressionBody.Types.f32:
                    writer.Write(e.Body.U.f32);
                    break;
                case ExpressionBody.Types.memory:
                    writer.WriteLEB(e.Body.U.memory.alignment_exponent);
                    writer.WriteLEB(e.Body.U.memory.offset);
                    break;
                case ExpressionBody.Types.type:
                    writer.Write((byte)e.Body.U.type);
                    break;
                case ExpressionBody.Types.br_table:
                    writer.WriteLEB((uint)e.Body.br_table.target_table.Length);
                    foreach (var t in e.Body.br_table.target_table)
                        writer.WriteLEB(t);
                    writer.WriteLEB(e.Body.br_table.default_target);
                    break;

                default:
                    throw new NotImplementedException();
            }

            if (e.Opcode == Opcodes.call_indirect)
                throw new NotImplementedException();

            if (e.Body.children != null) {
                Expression c;
                for (int i = 0; i < e.Body.children.Count; i++) {
                    c = e.Body.children[i];
                    EmitExpression(writer, ref c);
                }
            }
        }

        private static void GenerateStrippedFunctionBody (function_body body, BinaryWriter output) {
            output.WriteLEB((uint)0);

            var expr = new Expression {
                Opcode = Opcodes.unreachable,
                State = ExpressionState.Initialized
            };
            EmitExpression(output, ref expr);
            expr.Opcode = Opcodes.end;
            EmitExpression(output, ref expr);
        }

        private static void CopyExistingFunctionBody (byte[] bytes, function_body body, BinaryWriter output) {
            using (var fb = GetFunctionBodyStream(bytes, body)) {
                output.WriteLEB((uint)body.locals.Length);
                foreach (var l in body.locals) {
                    output.WriteLEB(l.count);
                    output.Write((byte)l.type);
                }

                output.Flush();
                fb.CopyTo(output.BaseStream);
            }
        }

        private static void DumpSections (Config config, byte[] wasmBytes, WasmReader wasmReader) {
            Directory.CreateDirectory(config.DumpSectionsPath);

            for (int i = 0; i < wasmReader.SectionHeaders.Count; i++) {
                var sh = wasmReader.SectionHeaders[i];
                var id = $"{i:00} {sh.id.ToString()} {sh.name ?? ""}".Trim();
                var path = Path.Combine(config.DumpSectionsPath, id);

                if (config.DumpSectionRegexes.Count > 0) {
                    if (!config.DumpSectionRegexes.Any((re) => {
                        if (sh.name != null)
                            if (re.IsMatch(sh.name))
                                return true;

                        return re.IsMatch(sh.id.ToString());
                    }))
                        continue;
                }

                try {
                    using (var outStream = File.OpenWrite(path)) {
                        outStream.SetLength(0);
                        using (var sw = GetSectionStream(wasmBytes, sh, false))
                            sw.CopyTo(outStream);
                    }
                } catch (Exception exc) {
                    Console.Error.WriteLine($"Failed to dump section {id}: {exc}");
                }
            }
        }

        private static void DumpFunctions (Config config, byte[] wasmBytes, WasmReader wasmReader, Dictionary<uint, FunctionInfo> functions) {
            Directory.CreateDirectory(config.DumpFunctionsPath);

            int count = 0;

            Parallel.ForEach(wasmReader.Code.bodies, (body) => {
                var i = Interlocked.Increment(ref count);
                if (((i % 5000) == 0) && (i > 0)) {
                    ClearLine($"Dumping functions... {i}/{functions.Count}");
                }

                var fi = functions[body.Index];
                var indexStr = $"#{body.Index:00000}";
                var name = fi.Name ?? indexStr;

                if (config.DumpFunctionRegexes.Count > 0) {
                    if (!config.DumpFunctionRegexes.Any((re) => re.IsMatch(name) || re.IsMatch(indexStr)))
                        return;
                }

                var fileName = name.Replace(":", "_").Replace("\\", "_").Replace("/", "_").Replace("<", "(").Replace(">", ")").Replace("?", "_").Replace("*", "_");
                if (fileName.Length > 127)
                    fileName = fileName.Substring(0, 127) + name.GetHashCode().ToString("X8");

                var path = Path.Combine(config.DumpFunctionsPath, fileName);

                var inBytes = new ArraySegment<byte>(wasmBytes, (int)body.StreamOffset, (int)(body.StreamEnd - body.StreamOffset));
                using (var fb = GetFunctionBodyStream(wasmBytes, body)) {
                    try {
                        if (config.DumpFunctions)
                            using (var outStream = File.OpenWrite(path)) {
                                outStream.SetLength(0);

                                fb.Position = 0;
                                fb.CopyTo(outStream);
                            }
                    } catch (Exception exc) {
                        Console.Error.WriteLine($"Failed to dump function {name}: {exc}");
                    }

                    path = Path.Combine(config.DumpFunctionsPath, fileName + ".dis");

                    try {
                        if (config.DisassembleFunctions)
                        using (var outStream = File.OpenWrite(path)) {
                            outStream.SetLength(0);

                            fb.Position = 0;
                            DisassembleFunctionBody(
                                name, fb, fi, inBytes, outStream, wasmReader.FunctionNames, functions, 
                                wasmReader.ImportedFunctionCount, wasmReader.Types.entries
                            );
                        }
                    } catch (Exception exc) {
                        Console.Error.WriteLine($"Failed to dump function {name}: {exc.Message}");
                    }
                }
            });
        }

        private class DisassembleListener : ExpressionReaderListener {
            const int BytesWidth = 16;

            int Depth;

            public Opcodes LastSeenOpcode;
            readonly uint FunctionIndexOffset;
            readonly FunctionInfo Function;
            readonly func_type[] Types;
            readonly Dictionary<uint, string> FunctionNames;
            readonly Dictionary<uint, FunctionInfo> Functions;
            readonly Stack<long> StartOffsets = new Stack<long>();
            readonly Stream Input;
            readonly ArraySegment<byte> InputBytes;
            readonly StreamWriter Output;

            public DisassembleListener (
                Stream input, ArraySegment<byte> inputBytes, StreamWriter output, FunctionInfo function,
                Dictionary<uint, string> functionNames, Dictionary<uint, FunctionInfo> functions, 
                uint functionIndexOffset, func_type[] types
            ) {
                FunctionIndexOffset = functionIndexOffset;
                Function = function;
                FunctionNames = functionNames;
                Functions = functions;
                Types = types;
                Input = input;
                InputBytes = inputBytes;
                Output = output;
                Depth = 0;
            }

            private string GetIndent (int offset, int depth) {
                const int threshold = 24;
                if (depth < threshold) {
                    return new string('.', (Depth * 2) + offset);
                } else {
                    var counter = $"{depth} > ";
                    return new string('.', (threshold * 2) + offset - counter.Length) + counter;
                }
            }

            private void WriteIndented (int offset, string text) {
                Output.Write("{0}{1}", GetIndent(offset, Depth), text);
            }

            private void WriteIndented (int offset, string format, params object[] args) {
                WriteIndented(offset, string.Format(format, args));
            }

            public void BeginBody (ref Expression expression, bool readingChildNodes) {
                if (readingChildNodes) {
                    Output.WriteLine();
                    WriteHeader(ref expression);
                    Output.WriteLine("(");
                }
            }

            public void BeginHeader () {
                StartOffsets.Push(Input.Position);
            }

            static string[] ByteStrings = new string[256];
            static DisassembleListener () {
                for (int i = 0; i < 256; i++)
                    ByteStrings[i] = i.ToString("X2");
            }

            char[] RangeBuffer = new char[10240];
            StringBuilder RangeBuilder = new StringBuilder();

            private void RangeToBytes (long startOffset, long endOffset, StreamWriter output) {
                var sb = RangeBuilder;
                sb.Clear();

                var position = Input.Position;
                var count = (int)(endOffset - startOffset);

                sb.AppendFormat("{0:0000} ", startOffset + InputBytes.Offset);

                int lineOffset = 0;
                for (int i = 0; i < count; i++) {
                    var b = InputBytes.Array[startOffset + i + InputBytes.Offset];
                    if (sb.Length - lineOffset >= BytesWidth) {
                        sb.AppendLine(" ...");
                        lineOffset = sb.Length;
                    }

                    sb.Append(ByteStrings[b]);
                }

                // HACK
                while (sb.Length - lineOffset < BytesWidth)
                    sb.Append(' ');

                sb.CopyTo(0, RangeBuffer, 0, sb.Length);
                output.Write(RangeBuffer, 0, sb.Length);
            }

            private void WriteHeader (ref Expression expression) {
                Depth -= 1;

                LastSeenOpcode = expression.Opcode;

                var startOffset = StartOffsets.Pop();
                var endOffset = Input.Position;

                RangeToBytes(startOffset, endOffset, Output);
                
                WriteIndented(0, expression.Opcode.ToString() + " ");

                if ((expression.Body.Type & ExpressionBody.Types.type) == ExpressionBody.Types.type)
                    Output.Write($"{expression.Body.U.type} ");

                Depth += 1;
            }

            private Wasm.Model.LanguageTypes GetTypeOfLocal (uint index) {
                if (index < Function.Type.param_types.Length)
                    return Function.Type.param_types[index];

                index -= (uint)Function.Type.param_types.Length;

                foreach (var l in Function.Body.locals) {
                    if (index < l.count)
                        return l.type;

                    index -= l.count;
                }

                return LanguageTypes.INVALID;
            }

            private string GetNameOfLocal (uint index) {
                if (index < Function.Type.param_types.Length) {
                    return $"arg{index}";
                }

                index -= (uint)Function.Type.param_types.Length;
                return $"local{index}";
            }

            public void EndBody (ref Expression expression, bool readChildNodes, bool successful) {
                if (!readChildNodes)
                    WriteHeader(ref expression);

                if (!successful) {
                    Output.Write("<error>");
                    Depth -= 1;
                } else if (readChildNodes) {
                    Depth -= 1;
                    WriteIndented(16, ")");
                    Output.WriteLine();
                    Output.WriteLine();
                } else {
                    switch (expression.Opcode) {
                        case Opcodes.call:
                            if (expression.Body.U.u32 >= FunctionIndexOffset) {
                                var funcIndex = expression.Body.U.u32 - FunctionIndexOffset;
                                if (!Functions.TryGetValue(funcIndex, out FunctionInfo func)) {
                                    Output.WriteLine($"<invalid #{funcIndex}>");
                                } else {
                                    Output.Write(func.Name ?? $"#{expression.Body.U.u32}");
                                    Output.Write(' ');
                                    Output.WriteLine(GetSignatureForType(func.Type));
                                }
                            } else {
                                FunctionNames.TryGetValue(expression.Body.U.u32, out var importName);
                                Output.WriteLine(importName ?? $"<import #{expression.Body.U.u32}>");
                            }
                            break;

                        case Opcodes.call_indirect:
                            var imm = expression.Body.U.call_indirect;
                            Output.WriteLine($"tables[{imm.table_index}] {GetSignatureForType(Types[imm.sig_index])}");
                            break;

                        case Opcodes.get_local:
                        case Opcodes.set_local:
                        case Opcodes.tee_local:
                            Output.WriteLine($"{GetTypeOfLocal(expression.Body.U.u32)} {GetNameOfLocal(expression.Body.U.u32)}");
                            break;

                        default:
                            switch (expression.Body.Type) {
                                case ExpressionBody.Types.u1:
                                    Output.WriteLine(expression.Body.U.u32);
                                    break;
                                case ExpressionBody.Types.u32:
                                    if (expression.Body.U.u32 >= 10)
                                        Output.WriteLine($"0x{expression.Body.U.u32:X8} {expression.Body.U.u32}");
                                    else
                                        Output.WriteLine(expression.Body.U.u32);
                                    break;
                                case ExpressionBody.Types.i64:
                                    Output.WriteLine($"0x{expression.Body.U.i64:X16} {expression.Body.U.i64}");
                                    break;
                                case ExpressionBody.Types.i32:
                                    if (expression.Body.U.u32 >= 10)
                                        Output.WriteLine($"0x{expression.Body.U.u32:X8} {expression.Body.U.i32}");
                                    else
                                        Output.WriteLine(expression.Body.U.i32);
                                    break;
                                case ExpressionBody.Types.f64:
                                    Output.WriteLine($"0x{expression.Body.U.u32:X16} {expression.Body.U.f64}");
                                    break;
                                case ExpressionBody.Types.f32:
                                    Output.WriteLine($"0x{expression.Body.U.u32:X8} {expression.Body.U.f32}");
                                    break;
                                case ExpressionBody.Types.memory:
                                    if (expression.Body.U.memory.alignment_exponent != 0)
                                        Output.WriteLine($"[{1 << (int)expression.Body.U.memory.alignment_exponent}] +{expression.Body.U.memory.offset}");
                                    else
                                        Output.WriteLine($"+{expression.Body.U.memory.offset}");
                                    break;
                                case ExpressionBody.Types.type:
                                    break;
                                case ExpressionBody.Types.br_table:
                                    Output.WriteLine("...");
                                    break;
                                default:
                                    Output.WriteLine();
                                    break;
                            }

                            break;
                    }

                    Depth -= 1;
                }
            }

            public void EndHeader (ref Expression expression, bool successful) {
                if (!successful) {
                    StartOffsets.Pop();
                    if (Depth == 0)
                        WriteIndented(0, "<eof>" + Environment.NewLine);
                    else
                        WriteIndented(Depth, "<error>" + Environment.NewLine);
                } else {
                    Depth += 1;
                }
            }
        }

        private static void DisassembleFunctionBody (
            string name, Stream stream, FunctionInfo function, ArraySegment<byte> inputBytes, FileStream outStream, 
            Dictionary<uint, string> functionNames, Dictionary<uint, FunctionInfo> functions, uint functionIndexOffset, func_type[] types
        ) {
            var body = function.Body;

            var outWriter = new StreamWriter(outStream, Encoding.UTF8);
            outWriter.WriteLine($"{name} ( {string.Join(", ", function.Type.param_types)} ) -> {function.Type.return_type} ({function.Body.body_size} byte(s))");
            if (body.locals.Length > 0) {
                outWriter.WriteLine($"{body.locals.Sum(l => l.count)} local(s)");
                foreach (var l in body.locals)
                    outWriter.WriteLine($"  {l.type} x{l.count}");
            }

            outWriter.WriteLine();

            try {
                var fbr = new BinaryReader(stream, Encoding.UTF8, true);
                var er = new ExpressionReader(fbr);

                var listener = new DisassembleListener(stream, inputBytes, outWriter, function, functionNames, functions, functionIndexOffset, types);

                while (true) {
                    Expression expr;
                    if (!er.TryReadExpression(out expr, listener))
                        break;
                    if (!er.TryReadExpressionBody(ref expr, listener))
                        break;
                }

                if (listener.LastSeenOpcode != Opcodes.end)
                    outWriter.WriteLine("ERROR: Function body did not end with an 'end' opcode");
                outWriter.Flush();
            } catch (Exception exc) {
                outWriter.WriteLine();
                outWriter.WriteLine("ERROR: Exception while disassembling function");
                outWriter.WriteLine(exc);
                outWriter.Flush();
                throw;
            }
        }

        private class AnalysisData {
            public readonly FunctionInfo[] Functions;
            public readonly Dictionary<string, NamespaceInfo> Namespaces;
            public readonly Dictionary<FunctionInfo, FunctionInfo[]> DirectDependencies;
            public readonly DependencyGraphNode[] DependencyGraph;
            public readonly int[] OpcodeCounts = new int[0xFFFF];
            public readonly Dictionary<string, object> RawData;

            public AnalysisData (Config config, byte[] wasmBytes, BinaryReader wasmStream, WasmReader wasmReader, FunctionInfo[] functions) {
                Functions = functions;
                Namespaces = ComputeNamespaceSizes(this);
                Console.Write(".");
                DirectDependencies = ComputeDirectDependencies(config, wasmBytes, wasmStream, wasmReader, this);
                Console.Write(".");
                DependencyGraph = ComputeDependencyGraph(config, wasmStream, wasmReader, this);
                Console.Write(".");
                RawData = ComputeRawData(config, wasmBytes, wasmStream, wasmReader, this);
            }

            public bool TryGetFunction (uint index, out FunctionInfo result) {
                result = default(FunctionInfo);
                if ((index < 0) || (index >= Functions.Length))
                    return false;
                result = Functions[index];
                return true;
            }
            
            private class RawDataListener : ExpressionReaderListener {
                public int[] OpcodeCounts;
                public int[] SmallBlockCounts = new int[8];
                public int AverageBlockLengthSum, BlockCount;
                public int GetLocalRuns, SetLocalRuns, DupCandidates, MaxRunSize, RunCount, AverageRunLengthSum;
                public int SimpleI32Memops;
                int CurrentRunSize;
                Expression PreviousExpression = default(Expression);

                public RawDataListener () {
                }

                public void BeginBody (ref Expression expression, bool readingChildNodes) {
                }

                public void BeginHeader () {
                }

                private void WriteHeader (ref Expression expression) {
                }

                private void ResetRun () {
                    if (CurrentRunSize != 0) {
                        AverageRunLengthSum += CurrentRunSize;
                        RunCount++;
                    }
                    CurrentRunSize = 0;
                }

                public void EndBody (ref Expression expression, bool readChildNodes, bool successful) {
                    OpcodeCounts[(int)expression.Opcode]++;

                    if (expression.Body.Type == ExpressionBody.Types.children) {
                        var count = expression.Body.children?.Count ?? 0;
                        BlockCount++;
                        if (count < SmallBlockCounts.Length)
                            SmallBlockCounts[count]++;
                        AverageBlockLengthSum += count;
                    }

                    var isLoad = (expression.Opcode >= OpcodesInfo.FirstLoad) && (expression.Opcode <= OpcodesInfo.LastLoad);
                    var isStore = (expression.Opcode >= OpcodesInfo.FirstStore) && (expression.Opcode <= OpcodesInfo.LastStore);

                    if (expression.Opcode == PreviousExpression.Opcode) {
                        if (expression.Opcode == Opcodes.get_local) {
                            GetLocalRuns++;
                            CurrentRunSize++;
                        } else if (expression.Opcode == Opcodes.set_local) {
                            SetLocalRuns++;
                            CurrentRunSize++;
                        } else {
                            ResetRun();
                        }
                    } else if (
                        (
                            // FIXME: Inaccurate
                            (expression.Opcode == Opcodes.get_local) ||
                            (expression.Opcode == Opcodes.get_global)
                        ) &&
                        (
                            (PreviousExpression.Opcode == Opcodes.set_local) ||
                            (PreviousExpression.Opcode == Opcodes.tee_local) ||
                            (PreviousExpression.Opcode == Opcodes.set_global)
                        )
                    ) {
                        ResetRun();
                        if (expression.Body.U.i32 == PreviousExpression.Body.U.i32)
                            DupCandidates++;
                    } else if (
                        (
                            (expression.Opcode == Opcodes.i32_load) ||
                            (expression.Opcode == Opcodes.i32_store)
                        ) &&
                        (
                            (PreviousExpression.Opcode == Opcodes.get_local) ||
                            (PreviousExpression.Opcode == Opcodes.tee_local) ||
                            (PreviousExpression.Opcode == Opcodes.get_global)
                        )
                    ) {
                        ResetRun();
                        SimpleI32Memops++;
                    } else {
                        ResetRun();
                    }

                    MaxRunSize = Math.Max(MaxRunSize, CurrentRunSize);
                    PreviousExpression = expression;
                }

                public void EndHeader (ref Expression expression, bool successful) {
                }
            }

            private Dictionary<string, object> ComputeRawData (Config config, byte[] wasmBytes, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData analysisData) {
                var listener = new RawDataListener {
                    OpcodeCounts = analysisData.OpcodeCounts
                };

                foreach (var function in analysisData.Functions) {
                    try {
                        using (var subStream = GetFunctionBodyStream(wasmBytes, function.Body)) {
                            var reader = new ExpressionReader(new BinaryReader(subStream));

                            Expression expr;
                            while (reader.TryReadExpression(out expr, listener)) {
                                if (!reader.TryReadExpressionBody(ref expr, listener))
                                    throw new Exception($"Failed to read body of {expr.Opcode}");
                            }
                        }
                    } catch (Exception exc) {
                        Console.Error.WriteLine($"Error analyzing function #{function.Index} '{function.Name}': {exc.Message}");
                    }
                }

                return new Dictionary<string, object> {
                    {"GetLocalRuns", listener.GetLocalRuns },
                    {"SetLocalRuns", listener.SetLocalRuns },
                    {"DupCandidates", listener.DupCandidates },
                    {"MaxRunSize", listener.MaxRunSize },
                    {"AverageRunLength", listener.AverageRunLengthSum / (double)listener.RunCount },
                    {"SimpleI32Memops", listener.SimpleI32Memops },
                    {"AverageBlockSize", listener.AverageBlockLengthSum / (double)listener.BlockCount },
                    {"Num2OpBlocks", listener.SmallBlockCounts[2] },
                    {"Num3OpBlocks", listener.SmallBlockCounts[3] },
                    {"Num4OpBlocks", listener.SmallBlockCounts[4] },
                };
            }
        }

        private static void GenerateGraph (
            Config config, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data, string path
        ) {
            EnsureValidPath(path);

            using (var output = new StreamWriter(path, false, Encoding.UTF8)) {
                output.WriteLine("digraph namespaces {");

                const int maxLength = 24;
                const int minCount = 2;

                var namespaceDependencies = data.DependencyGraph.Where(dgn => dgn.NamespaceName != null).ToLookup(dgn => dgn.NamespaceName);

                var referencedNamespaces = new HashSet<NamespaceInfo>(NamespaceInfo.Comparer);
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

                var namespaces = new HashSet<NamespaceInfo>(NamespaceInfo.Comparer);
                foreach (var kvp in data.Namespaces) {
                    namespaces.Add(kvp.Value);

                    if (namespaceDependencies.Contains(kvp.Key)) {
                        var dgn = namespaceDependencies[kvp.Key].First();

                        foreach (var rn in dgn.ReferencedNamespaces)
                            namespaces.Add(data.Namespaces[rn.NamespaceName]);
                    }
                }

                var labelsNeeded = new HashSet<NamespaceInfo>(NamespaceInfo.Comparer);

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
            Config config, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data, string path
        ) {
            EnsureValidPath(path);

            using (var output = new StreamWriter(path, false, Encoding.UTF8)) {
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
                    WriteCell(output, "String", $"{sh.id} ({(byte)sh.id}) {(string.IsNullOrWhiteSpace(sh.name) ? "" : " '" + sh.name + "'")}");
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

                foreach (var fn in data.Functions) {
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
                    new[] { 80, 500, 50, 50, 60, 70, 70, 80, 60, 60 },
                    new[] { "Type", "Name", "In", "Out", "Out (NS)", "Size", "Out (Deep)", "Size (Deep)", "Exported", "Addressible" }
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
                    WriteCell(output, "String", entry.TimesExported > 0 ? "yes" : "no");
                    WriteCell(output, "String", entry.TimesInTable > 0 ? "yes" : "no");
                    output.WriteLine("            </Row>");
                }

                WriteSheetFooter(output, 10);

                WriteSheetHeader(
                    output, "Raw Data",
                    new[] { 500, 100 },
                    new[] { "Name", "Value" }
                );

                foreach (var kvp in data.RawData) {
                    output.WriteLine("            <Row>");
                    WriteCell(output, "String", kvp.Key.ToString());
                    WriteCell(output, kvp.Value is string ? "String" : "Number", kvp.Value.ToString());
                    output.WriteLine("            </Row>");
                }

                WriteSheetFooter(output, 2);

                WriteSheetHeader(
                    output, "Opcode Counts",
                    new[] { 500, 100 },
                    new[] { "Name", "Count" }
                );

                for (i = 0; i < data.OpcodeCounts.Length; i++) {
                    if (data.OpcodeCounts[i] <= 0)
                        continue;
                    var name = ((Opcodes)i).ToString();
                    output.WriteLine("            <Row>");
                    WriteCell(output, "String", name);
                    WriteCell(output, "Number", data.OpcodeCounts[i].ToString());
                    output.WriteLine("            </Row>");
                }

                WriteSheetFooter(output, 2);

                WriteSheetHeader(
                    output, "Import Export",
                    new[] { 100, 100, 400, 100 },
                    new[] { "Index", "Kind", "Path", "Type" }
                );

                for (int j = 0; j < wasmReader.Imports.entries.Length; j++) {
                    var entry = wasmReader.Imports.entries[j];
                    output.WriteLine("            <Row>");
                    WriteCell(output, "Number", j.ToString());
                    WriteCell(output, "String", $"import {entry.kind}");
                    WriteCell(output, "String", $"{entry.module}.{entry.field}");
                    switch (entry.kind) {
                        case external_kind.Function:
                            var ft = wasmReader.Types.entries[entry.type.Function];
                            WriteCell(output, "String", GetSignatureForType(ft));
                            break;
                        case external_kind.Global:
                            WriteCell(output, "String", entry.type.Global.content_type.ToString());
                            break;
                        default:
                            WriteCell(output, "String", "");
                            break;
                    }
                    output.WriteLine("            </Row>");
                }

                for (int j = 0; j < wasmReader.Exports.entries.Length; j++) {
                    var entry = wasmReader.Exports.entries[j];
                    output.WriteLine("            <Row>");
                    WriteCell(output, "Number", j.ToString());
                    WriteCell(output, "String", $"export {entry.kind}");
                    WriteCell(output, "String", $"{entry.field}");
                    switch (entry.kind) {
                        case external_kind.Function:
                            var relativeIndex = entry.index - wasmReader.ImportedFunctionCount;
                            if ((relativeIndex >= 0) && (relativeIndex < data.Functions.Length)) {
                                var fn = data.Functions[relativeIndex];
                                WriteCell(output, "String", GetSignatureForType(fn.Type));
                            } else {
                                WriteCell(output, "String", "?");
                            }
                            break;
                        default:
                            WriteCell(output, "String", "");
                            break;
                    }
                    output.WriteLine("            </Row>");
                }

                WriteSheetFooter(output, 4);

                output.WriteLine("</Workbook>");
            }
        }

        class DependencyGraphNode {
            public static readonly ReferenceComparer<DependencyGraphNode> Comparer = new ReferenceComparer<DependencyGraphNode>();

            public FunctionInfo Function;
            public string NamespaceName;

            public uint ShallowSize;
            public uint DeepSize;

            public DependencyGraphNode[] DirectDependencies;
            public int DeepDependencies;

            public int Recursions;
            public int Dependents;

            public int TimesExported, TimesInTable;

            public DependencyGraphNode ParentNamespace;
            public HashSet<DependencyGraphNode> ChildFunctions;
            public HashSet<DependencyGraphNode> ReferencedNamespaces;

            public int DirectDependencyCount {
                get {
                    if (Function != null)
                        return DirectDependencies?.Length ?? 0;

                    var hs = new HashSet<DependencyGraphNode>(DependencyGraphNode.Comparer);
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
            Config config, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data
        ) {
            var namespaceNodes = new Dictionary<string, DependencyGraphNode>(data.Functions.Length / 10, StringComparer.Ordinal);
            var functionNodes = new DependencyGraphNode[data.Functions.Length];

            foreach (var fn in data.Functions) {
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
                            ChildFunctions = new HashSet<DependencyGraphNode>(DependencyGraphNode.Comparer),
                            ReferencedNamespaces = new HashSet<DependencyGraphNode>(DependencyGraphNode.Comparer)
                        };

                    namespaceNode.ShallowSize += fnode.ShallowSize;
                    namespaceNode.ChildFunctions.Add(fnode);
                    fnode.ParentNamespace = namespaceNode;
                }

                functionNodes[fn.Index] = fnode;
            }

            if ((wasmReader.Tables.entries?.Length ?? 0) > 1)
                throw new NotImplementedException($"Wasm spec only allows at most 1 table but this module contains {wasmReader.Tables.entries.Length} tables");
            else if ((wasmReader.Tables.entries?.FirstOrDefault().element_type ?? LanguageTypes.anyfunc) != LanguageTypes.anyfunc)
                throw new NotImplementedException($"Table of type {wasmReader.Tables.entries[0].element_type} not implemented");

            var functionIndexOffset = wasmReader.FunctionIndexOffset;

            var table = new uint[1];
            foreach (var elem in wasmReader.Elements.entries) {
                // HACK: This is not spec-compliant
                if (elem.index != 0)
                    continue;

                if (elem.offset.Opcode != Opcodes.i32_const)
                    throw new NotImplementedException($"Unexpected elements offset {elem.offset.Opcode}");

                var startOffset = elem.offset.Body.U.i32;
                var endOffset = startOffset + elem.elems.Length;
                if (endOffset >= table.Length)
                    Array.Resize(ref table, endOffset);

                Array.Copy(elem.elems, 0, table, startOffset, elem.elems.Length);
            }

            // Scan the function pointer table and record function references inside it
            foreach (var elem in table) {
                if (elem < functionIndexOffset)
                    continue;

                var adjustedIndex = elem - functionIndexOffset;

                FunctionInfo fi;
                // FIXME: Abort if not found?
                if (!data.TryGetFunction(adjustedIndex, out fi))
                    continue;

                var fn = functionNodes[fi.Index];
                fn.TimesInTable += 1;
            }

            foreach (var export in wasmReader.Exports.entries) {
                if (export.kind != external_kind.Function)
                    continue;

                if (export.index < functionIndexOffset)
                    continue;

                var adjustedIndex = export.index - functionIndexOffset;

                FunctionInfo fi;
                // FIXME: Abort if not found?
                if (!data.TryGetFunction(adjustedIndex, out fi))
                    continue;

                var fn = functionNodes[fi.Index];
                fn.TimesExported += 1;
            }

            // TODO: Record exports as well

            for (uint i = 0; i < functionNodes.Length; i++) {
                data.TryGetFunction(i, out FunctionInfo fi);
                var node = functionNodes[i];
                FunctionInfo[] dds;
                if (!data.DirectDependencies.TryGetValue(fi, out dds))
                    continue;

                node.DirectDependencies = (from dd in dds select functionNodes[dd.Index]).ToArray();

                foreach (var dep in node.DirectDependencies) {
                    if (dep.Function.Index == i) {
                        node.Recursions += 1;
                        continue;
                    }

                    if ((node.ParentNamespace != null) && (dep.ParentNamespace != null))
                        node.ParentNamespace.ReferencedNamespaces.Add(dep.ParentNamespace);

                    // Propagate dependencies upward
                    var upward = functionNodes[dep.Function.Index];
                    upward.Dependents += 1;

                    if (upward.ParentNamespace != null)
                        upward.ParentNamespace.Dependents += 1;
                }
            }

            Parallel.ForEach(functionNodes, (node) => ComputeDeepDependencies(config, data, functionNodes, node));
            Parallel.ForEach(namespaceNodes, (kvp) => ComputeDeepDependencies(config, data, functionNodes, kvp.Value));

            return (from nsn in namespaceNodes.Values where nsn.ChildFunctions.Count > 1 select nsn).Concat(functionNodes).ToArray();
        }

        private static ThreadLocal<bool[]> SeenBits = new ThreadLocal<bool[]>();

        private static void ComputeDeepDependencies (
            Config config, AnalysisData data,
            DependencyGraphNode[] functionNodes,
            DependencyGraphNode node
        ) {
            if (node.Function != null)
                node.DeepSize = node.Function.Body.body_size;
            else
                node.DeepSize = 0;

            var seenBits = SeenBits.Value;
            if ((seenBits == null) || (seenBits.Length != data.Functions.Length))
                seenBits = SeenBits.Value = new bool[data.Functions.Length];
            else
                Array.Clear(seenBits, 0, seenBits.Length);
            var todoList = new Queue<FunctionInfo>();

            if (node.DirectDependencies != null) {
                foreach (var dep in node.DirectDependencies) {
                    seenBits[dep.Function.Index] = true;
                    todoList.Enqueue(dep.Function);
                }
            } else if (node.ChildFunctions != null) {
                foreach (var cf in node.ChildFunctions) {
                    seenBits[cf.Function.Index] = true;
                    todoList.Enqueue(cf.Function);
                }
            }

            while (todoList.Count > 0) {
                var dep = todoList.Dequeue();

                node.DeepSize += dep.Body.body_size;
                node.DeepDependencies += 1;

                var depNode = functionNodes[dep.Index];
                if (depNode.DirectDependencies == null)
                    continue;

                foreach (var subDep in depNode.DirectDependencies) {
                    if (seenBits[subDep.Function.Index])
                        continue;

                    seenBits[subDep.Function.Index] = true;
                    todoList.Enqueue(subDep.Function);
                }
            }
        }

        private static Dictionary<FunctionInfo, FunctionInfo[]> ComputeDirectDependencies (
            Config config, byte[] wasmBytes, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data
        ) {
            var result = new Dictionary<FunctionInfo, FunctionInfo[]>(data.Functions.Length, FunctionInfo.Comparer);
            var temp = new HashSet<FunctionInfo>(FunctionInfo.Comparer);

            foreach (var fn in data.Functions) {
                temp.Clear();
                try {
                    GatherDirectDependencies(config, wasmBytes, wasmStream, wasmReader, data, fn, temp);

                    if (temp.Count > 0)
                        result[fn] = temp.ToArray();
                } catch (Exception exc) {
                    Console.Error.WriteLine($"Error while analyzing function #{fn.Index} {fn.Name}: {exc.Message}");
                }
            }

            return result;
        }

        private static Stream GetFunctionBodyStream (byte[] bytes, function_body function) {
            var size = (int)(function.StreamEnd - function.StreamOffset);
            var result = new MemoryStream(bytes, (int)function.StreamOffset, size, false);
            return result;
        }

        private class GatherDirectDependenciesVisitor : IExpressionVisitor {
            public WasmReader wasmReader;
            public AnalysisData data;
            public HashSet<FunctionInfo> dependencies;

            public void Visit (ref Expression expr, int depth, out bool wantToVisitChildren) {
                if (expr.Opcode == Opcodes.call) {
                    var index = expr.Body.U.u32;
                    if (index < wasmReader.FunctionIndexOffset) {
                        // Calling an import
                    } else {
                        index -= wasmReader.FunctionIndexOffset;
                        FunctionInfo callee;
                        if (!data.TryGetFunction(index, out callee))
                            throw new Exception($"Invalid call target: {index}");
                        else
                            dependencies.Add(callee);
                    }
                }

                wantToVisitChildren = true;
            }
        }

        private static void GatherDirectDependencies (
            Config config, byte[] wasmBytes, BinaryReader wasmStream, WasmReader wasmReader, AnalysisData data, 
            FunctionInfo function, HashSet<FunctionInfo> dependencies
        ) {
            using (var subStream = GetFunctionBodyStream(wasmBytes, function.Body)) {
                var reader = new ExpressionReader(new BinaryReader(subStream));
                var visitor = new GatherDirectDependenciesVisitor {
                    wasmReader = wasmReader,
                    data = data,
                    dependencies = dependencies
                };

                Expression expr;
                while (reader.TryReadExpression(out expr)) {
                    if (!reader.TryReadExpressionBody(ref expr))
                        throw new Exception($"Failed to read body of {expr.Opcode}");

                    Expression.Visit(ref expr, visitor);
                }
            }
        }

        private static Dictionary<string, NamespaceInfo> ComputeNamespaceSizes (AnalysisData data) {
            var namespaces = new Dictionary<string, NamespaceInfo>(StringComparer.Ordinal);
            int i = 0;
            foreach (var fn in data.Functions) {
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

            if (operand != null) {
                operand = operand.Replace("\\\"", "\"");

                if (operand.StartsWith('"') && operand.EndsWith('"'))
                    operand = operand.Substring(1, operand.Length - 2);
            }

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
                case "stripreport":
                case "stripreportout":
                case "stripreportoutput":
                case "stripreportpath":
                    config.StripReportPath = operand;
                    break;
                case "verify":
                    config.VerifyOutput = true;
                    break;
                case "noverify":
                    config.VerifyOutput = false;
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
                        config.DumpSectionRegexes.Add(new Regex(".*"));
                    else
                        config.DumpSectionRegexes.Add(new Regex(operand));
                    break;
                case "dumpsectionsout":
                case "dumpsectionsoutput":
                case "dumpsectionsto":
                case "dumpsectionspath":
                    config.DumpSectionsPath = operand;
                    break;
                case "dumpfunctions":
                    if (string.IsNullOrWhiteSpace(operand))
                        config.DumpFunctionRegexes.Add(new Regex(".*"));
                    else
                        config.DumpFunctionRegexes.Add(new Regex(operand));
                    break;
                case "dumpfunctionsout":
                case "dumpfunctionsoutput":
                case "dumpfunctionsto":
                case "dumpfunctionspath":
                    config.DumpFunctionsPath = operand;
                    break;
                case "disassembleonly":
                case "disonly":
                    config.DumpFunctions = false;
                    break;
                case "dumponly":
                    config.DisassembleFunctions = false;
                    break;
                default:
                    Console.Error.WriteLine($"Invalid argument: '{arg}'");
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
