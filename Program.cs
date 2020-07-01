using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Gee.External.Capstone.X86;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wasm.Model;

namespace WasmStrip {
    class Config {
        public string Mode, ModulePath;
    }

    class Program {
        public static int Main (string[] _args) {
            var args = new List<string> (_args);
            if (args.Length != 1) {
                Console.Error.WriteLine("Usage: WasmStrip module.wasm --mode=mode [--option ...] [@response.rsp]");
                return 1;
            }

            var config = new Config();
            var argErrorCount = 0;

            for (int i = 0; i < args.Count; i++) {
                var arg = args[i];
                if (arg[0] == '@') {
                    try {
                        ParseResponseFile(arg[0].Substring(1), args);
                    } catch (Exception exc) {
                        Console.Error.WriteLine($"Error parsing response file '{arg}': {exc}");
                        argErrorCount++;
                    }
                    args.RemoveAt(i--);
                } else if (arg.StartsWith("--")) {
                    ParseOption(arg, config);
                    args.RemoveAt(i--);
                } else {
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

            config.ModulePath = args[0];
            if (!File.Exists(config.ModulePath)) {
                Console.Error.WriteLine($"File not found: {config.ModulePath}");
                return 3;
            }

            if (config.Mode == null) {
                Console.Error.WriteLine("No mode specified");
                return 4;
            }

            Console.WriteLine($"Processing module {config.ModulePath}...");

            var wasmStream = new BinaryReader(File.OpenRead(config.ModulePath), System.Text.Encoding.UTF8, false);
            WasmReader wasmReader;
            using (wasmStream) {
                wasmReader = new WasmReader(wasmStream);
                wasmReader.Read();
            }

            if (Debugger.IsAttached) {
                Console.WriteLine("Press enter to exit");
                Console.ReadLine();
            }

            return 0;
        }

        public static void ParseResponseFile (string path, List<string> args) {
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
