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
        public int EstimatedLinearStackSize;
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

                var wasmStream = new BinaryReader(File.OpenRead(config.ModulePath), System.Text.Encoding.UTF8, false);
                var functions = new Dictionary<uint, FunctionInfo>();
                WasmReader wasmReader;
                using (wasmStream) {
                    wasmReader = new WasmReader(wasmStream);
                    wasmReader.FunctionBodyCallback = (fb, br) => {
                        var info = ProcessFunctionBody(wasmReader, fb, br, config);
                        functions[info.Index] = info;
                    };
                    wasmReader.Read();
                }
            } finally {
                if (!execStarted)
                    Console.Error.WriteLine("Usage: WasmStrip module.wasm --mode=mode [--option ...] [@response.rsp]");

                if (Debugger.IsAttached) {
                    Console.WriteLine("Press enter to exit");
                    Console.ReadLine();
                }
            }

            return 0;
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
            result.EstimatedLinearStackSize = GetLinearStackSizeForFunction(fb, result.Type, br);

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

        private static int GetLinearStackSizeForFunction (function_body fb, func_type type, BinaryReader br) {
            try {
                using (var subStream = new ModuleSaw.StreamWindow(fb.Stream, fb.StreamOffset, fb.StreamEnd - fb.StreamOffset)) {
                    var reader = new ExpressionReader(new BinaryReader(subStream));

                    Expression expr;
                    Opcodes previous = Opcodes.end;

                    var localCount = fb.locals.Sum(l => l.count) + type.param_types.Length;
                    var locals = new Expression[localCount];
                    Expression global0 = MakeI32Const(0);
                    var stack = new Stack<Expression>();

                    int num_read = 0;
                    while (reader.TryReadExpression(out expr) && num_read < 20) {
                        if (!reader.TryReadExpressionBody(ref expr))
                            throw new Exception("Failed to read body of " + expr.Opcode);

                        num_read++;

                        ProcessExpressionForStackSizeCalculation(fb, expr, locals, ref global0, stack, ref num_read);

                        previous = expr.Opcode;
                    }

                    return Math.Abs(global0.Body.U.i32);
                }
            } catch (Exception exc) {
                Console.Error.WriteLine ($"Error determining linear stack size: {exc}");
                return 0;
            }
        }

        private static void ProcessExpressionForStackSizeCalculation (function_body fb, Expression expr, Expression[] locals, ref Expression global0, Stack<Expression> stack, ref int num_read) {
            if (fb.Index == 1749)
                ;

            switch (expr.Opcode) {
                case Opcodes.i32_const:
                case Opcodes.i64_const:
                case Opcodes.f32_const:
                case Opcodes.f64_const:
                    stack.Push(expr);
                    break;
                case Opcodes.get_global:
                    // HACK
                    stack.Push(MakeI32Const(0));
                    break;
                case Opcodes.set_global:
                    if (expr.Body.U.u32 == 0) {
                        global0 = stack.Pop();
                        num_read = 9999;
                    } else
                        stack.Pop();
                    break;
                case Opcodes.get_local:
                    stack.Push(locals[expr.Body.U.u32]);
                    break;
                case Opcodes.set_local:
                    locals[expr.Body.U.u32] = stack.Pop();
                    break;
                case Opcodes.tee_local:
                    locals[expr.Body.U.u32] = stack.Peek();
                    break;
                case Opcodes.i32_load:
                    stack.Pop();
                    // FIXME
                    stack.Push(MakeI32Const(int.MinValue));
                    break;
                case Opcodes.i32_store:
                    stack.Pop();
                    break;
                case Opcodes.i32_add:
                case Opcodes.i32_sub: {
                        var a = stack.Pop().Body.U.i32;
                        var b = stack.Pop().Body.U.i32;
                        stack.Push(MakeI32Const(
                            a + (b * (expr.Opcode == Opcodes.i32_sub ? -1 : 1))
                        ));
                        break;
                    }
                case Opcodes.block:
                    foreach (var child in expr.Body.children) {
                        ProcessExpressionForStackSizeCalculation(fb, child, locals, ref global0, stack, ref num_read);
                        if (num_read >= 9999)
                            break;
                    }

                    break;
                case Opcodes.end:
                    break;
                // Not implemented
                case Opcodes.call:
                case Opcodes.call_indirect:
                default:
                    num_read = 9999;
                    break;
            }
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
                    config.StripRetainRegexes.Add(operand);
                    break;
                case "strip":
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
