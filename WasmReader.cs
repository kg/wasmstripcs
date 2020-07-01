using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ModuleSaw;
using Wasm.Model;

namespace WasmStrip {
    public class WasmReader {
        public readonly BinaryReader Input;

        public TypeSection Types;
        public ImportSection Imports;
        public FunctionSection Functions;
        public TableSection Tables;
        public GlobalSection Globals;
        public ExportSection Exports;
        public ElementSection Elements;
        public CodeSection Code;

        public readonly Dictionary<uint, string> FunctionNames = new Dictionary<uint, string>();

        public WasmReader (BinaryReader input) {
            Input = input;
        }

        public void Read () {
            var reader = new ModuleReader(Input);

            Program.Assert(reader.ReadHeader(), "ReadHeader");

            SectionHeader sh;

            while (reader.ReadSectionHeader(out sh)) {
                if (sh.StreamPayloadEnd > reader.Reader.BaseStream.Length)
                    throw new Exception("Invalid header");

                switch (sh.id) {
                    case SectionTypes.Type:
                        Program.Assert(reader.ReadTypeSection(out Types));
                        break;

                    case SectionTypes.Import:
                        Program.Assert(reader.ReadImportSection(out Imports));
                        break;

                    case SectionTypes.Function:
                        Program.Assert(reader.ReadFunctionSection(out Functions));
                        break;

                    case SectionTypes.Table:
                        // FIXME: Not tested
                        Program.Assert(reader.ReadTableSection(out Tables));
                        break;

                    case SectionTypes.Global:
                        Program.Assert(reader.ReadGlobalSection(out Globals));
                        break;

                    case SectionTypes.Export:
                        Program.Assert(reader.ReadExportSection(out Exports));
                        break;

                    case SectionTypes.Element:
                        Program.Assert(reader.ReadElementSection(out Elements));
                        break;

                    case SectionTypes.Code:
                        Program.Assert(reader.ReadCodeSection(out Code));
                        break;

                    case SectionTypes.Data:
                        DataSection ds;
                        Program.Assert(reader.ReadDataSection(out ds));
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
