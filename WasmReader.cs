using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ModuleSaw;
using Wasm.Model;

namespace WasmStrip {
    public class WasmReader {
        public readonly BinaryReader Input;
        public bool Tracing;

        public TypeSection Types;
        public ImportSection Imports = new ImportSection {
            entries = Array.Empty<import_entry>(),
        };
        public FunctionSection Functions;
        public TableSection Tables;
        public GlobalSection Globals = new GlobalSection {
            globals = Array.Empty<global_variable>(),
        };
        public ExportSection Exports = new ExportSection {
            entries = Array.Empty<export_entry>(),
        };
        public ElementSection Elements;
        public CodeSection Code;

        public Action<function_body, BinaryReader> FunctionBodyCallback = null;

        public readonly List<SectionHeader> SectionHeaders = new List<SectionHeader>();
        public readonly Dictionary<uint, string> FunctionNames = new Dictionary<uint, string>();

        public WasmReader (BinaryReader input) {
            Input = input;
        }

        private uint? _ImportedFunctionCount,
            _ExportedFunctionCount;

        public uint FunctionIndexOffset {
            get {
                return ImportedFunctionCount;
            }
        }

        public uint ImportedFunctionCount {
            get {
                if (!_ImportedFunctionCount.HasValue)
                    _ImportedFunctionCount = (uint)Imports.entries.Count(e => e.kind == external_kind.Function);
                return _ImportedFunctionCount.Value;
            }
        }

        public uint ExportedFunctionCount {
            get {
                if (!_ExportedFunctionCount.HasValue)
                    _ExportedFunctionCount = (uint)Exports.entries.Count(e => e.kind == external_kind.Function);
                return _ExportedFunctionCount.Value;
            }
        }

        public void Read () {
            var reader = new ModuleReader(Input) {
                Tracing = Tracing
            };

            Program.Assert(reader.ReadHeader(), "ReadHeader");

            SectionHeader sh;

            while (reader.ReadSectionHeader(out sh)) {
                if (sh.StreamPayloadEnd > reader.Reader.BaseStream.Length)
                    throw new Exception("Invalid header");

                SectionHeaders.Add(sh);

                try {
                    switch (sh.id) {
                        case SectionTypes.Type:
                            reader.ReadTypeSection(out Types);
                            break;

                        case SectionTypes.Import:
                            reader.ReadImportSection(out Imports);
                            break;

                        case SectionTypes.Function:
                            reader.ReadFunctionSection(out Functions);
                            break;

                        case SectionTypes.Table:
                            // FIXME: Not tested
                            reader.ReadTableSection(out Tables);
                            break;

                        case SectionTypes.Global:
                            reader.ReadGlobalSection(out Globals);
                            break;

                        case SectionTypes.Export:
                            reader.ReadExportSection(out Exports);
                            break;

                        case SectionTypes.Element:
                            reader.ReadElementSection(out Elements);
                            break;

                        case SectionTypes.Code:
                            reader.ReadCodeSection(out Code, FunctionBodyCallback);
                            break;

                        case SectionTypes.Data:
                            DataSection ds;
                            reader.ReadDataSection(out ds);
                            Input.BaseStream.Seek(sh.StreamPayloadEnd, SeekOrigin.Begin);
                            break;
                    
                        case SectionTypes.Custom:
                            if (sh.name == "name")
                                ReadNameSection(reader, Input, sh);
                            Input.BaseStream.Seek(sh.StreamPayloadEnd, SeekOrigin.Begin);
                            break;

                        default:
                            Input.BaseStream.Seek(sh.StreamPayloadEnd, SeekOrigin.Begin);
                            break;
                    }
                } catch (Exception exc) {
                    throw new Exception($"Error reading {sh.id} section", exc);
                }
            }
        }

        private void ReadNameSection (ModuleReader reader, BinaryReader sr, SectionHeader nameSectionHeader) {
            var bs = sr.BaseStream;
            while ((bs.Position < nameSectionHeader.StreamPayloadEnd) && (bs.Position < bs.Length)) {
                var id = reader.Reader.ReadByte();
                var size = (uint)reader.Reader.ReadLEBUInt();
                switch (id) {
                    // Function names
                    case 1:
                        reader.ReadList((i) => {
                            var idx = (uint)reader.Reader.ReadLEBUInt();
                            var name = reader.Reader.ReadPString();

                            FunctionNames.Add(idx, name);

                            return (object)null;
                        });
                        break;

                    // Module name
                    case 0:
                    // Local names
                    case 2:
                    default:
                        sr.BaseStream.Seek(size, SeekOrigin.Current);
                        break;
                }
            }
        }
    }
}
