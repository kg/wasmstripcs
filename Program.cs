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
    }

    class FunctionInfo {
        public uint Index;
        public uint TypeIndex;
        public func_type Type;
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

                    if (config.ReportPath != null)
                        GenerateReport(config, wasmStream, wasmReader, functions);
                }
            } finally {
                if (!execStarted) {
                    Console.Error.WriteLine("Usage: WasmStrip module.wasm [--option ...] [@response.rsp]");
                    Console.Error.WriteLine("  --reportout=filename.csv");
                    Console.Error.WriteLine("  --diffagainst=oldmodule.wasm --diffout=filename.csv");
                    Console.Error.WriteLine("  --out=newmodule.wasm ...");
                    Console.Error.WriteLine("    --strip=regex [...]");
                    Console.Error.WriteLine("    --striplist=regexes.txt");
                    Console.Error.WriteLine("    --retain=regex [...]");
                    Console.Error.WriteLine("    --retainlist=regexes.txt");
                }

                if (Debugger.IsAttached) {
                    Console.WriteLine("Press enter to exit");
                    Console.ReadLine();
                }
            }

            return 0;
        }

        private static void GenerateReport (Config config, BinaryReader wasmStream, WasmReader wasmReader, Dictionary<uint, FunctionInfo> functions) {
            using (var output = new StreamWriter(config.ReportPath, false, Encoding.UTF8)) {
            }
        }

        public static FunctionInfo ProcessFunctionBody (WasmReader wr, function_body fb, BinaryReader br, Config config) {
            var localsSize = 0;
            var numLocals = 0;
            uint typeIndex;

            var result = new FunctionInfo {
                Index = fb.Index,
                TypeIndex = (typeIndex = wr.Functions.types[fb.Index]),
                Type = wr.Types.entries[typeIndex]
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
