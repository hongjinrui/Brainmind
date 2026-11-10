"""Reproduce selected format evidence from the official DLL without executing it.

Development-only dependencies: dnfile, dncil. Does not install/run TheBrain.
The metadata row numbers below apply only to the SHA-256 checked build.
"""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import struct

EXPECTED_SHA256 = "70356c4e3c243b8072d839754e69d34346803dec4a3c38dca336d2fb0a50d2fa"
# Offsets are relative to method-body start, INCLUDING its header.
SECTIONS = {
    570: [(0x22, 0x71)],       # Creation defaults: Label, Kind-dependent icon.
    607: [(0x23, 0xAC), (0x2B1, 0x31F)],  # Notes selection and constructor args.
    1782: [(0, 0x100)],       # Attachment-is-note predicate.
    2170: [(0, 0x100)],       # New UUID and UTC creation/modification times.
    2201: [(0, 0x100)], 2202: [(0, 0x100)], # Raw cache serialization/parsing.
    2608: [(0xC5, 0x1AB)], 2609: [(0, 0x100)],
    2610: [(0, 0x100)],       # Seven-field serialization.
    3367: [(0, 0x100)],       # Note selection orders by ModificationDateTime.
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assembly", type=Path)
    args = parser.parse_args()
    digest = hashlib.sha256(args.assembly.read_bytes()).hexdigest()
    if digest != EXPECTED_SHA256:
        parser.error("Unexpected DLL SHA-256; fixed metadata IDs must not be applied to another build")
    try:
        import dnfile
        from dncil.cil.body.reader import read_method_body_from_bytes
    except ImportError:
        parser.error("Install dnfile and dncil in a local development venv first")
    pe = dnfile.dnPE(str(args.assembly))
    tables = pe.net.mdtables
    owners = {}
    for row_id, owner in enumerate(tables.TypeDef.rows, 1):
        for field in owner.FieldList:
            owners[("Field", field.row_index)] = row_id
        for method in owner.MethodList:
            owners[("MethodDef", method.row_index)] = row_id

    def body(row_id):
        method = tables.MethodDef.rows[row_id - 1]
        return read_method_body_from_bytes(pe.get_data(method.Rva))

    def token_name(operand):
        if not hasattr(operand, "value"):
            return "" if operand is None else str(operand)
        number = operand.value
        kind, row_id = number >> 24, number & 0xFFFFFF
        if kind == 0x70:
            return repr(pe.net.user_strings.get(row_id).value)
        name = {0x01: "TypeRef", 0x02: "TypeDef", 0x04: "Field", 0x06: "MethodDef",
                0x0A: "MemberRef", 0x1B: "TypeSpec", 0x2B: "MethodSpec"}.get(kind)
        if name is None:
            return str(operand)
        row = getattr(tables, name).rows[row_id - 1]
        title = str(getattr(row, "Name", getattr(row, "TypeName", "?")))
        if name == "MemberRef":
            title = str(getattr(row.Class.row, "TypeName", "?")) + "." + title
        elif name == "MethodSpec":
            title = str(row.Method.row.Name)
        owner = owners.get((name, row_id))
        return f"{name}[{row_id}]" + (f" TypeDef[{owner}]" if owner else "") + f" {title}"

    print("SHA-256:", digest)
    constants = {id(row.Parent.row): row for row in tables.Constant.rows}
    for row_id, label in ((181, "ThoughtKind"), (225, "NoteType"), (228, "FieldDataType")):
        row = tables.TypeDef.rows[row_id - 1]
        print(f"\n{label}: TypeDef[{row_id}]")
        for field in row.FieldList:
            constant = constants.get(id(field.row))
            if constant:
                print(" ", field.row.Name, struct.unpack("<i", constant.Value.value)[0])
    print("\nFieldDefinition DataType signature:", tables.TypeDef.rows[244].FieldList[1].row.Signature.value.hex())
    print("FieldInstance storage fields:", [str(x.row.Name) for x in tables.TypeDef.rows[245].FieldList])
    for spec_id in (107, 108, 109):
        print("MethodSpec", spec_id, tables.MethodSpec.rows[spec_id - 1].Method.row.Name)

    # Static evaluation of ONLY the note-name constants. MethodDef2695 is a
    # char XOR loop using sum(key[:4]) and index*7 modulo255, not execution.
    def note_literal(method_id, text_offset, key_offset):
        operands = {i.offset: i.operand for i in body(method_id).instructions}
        def string_at(offset):
            return pe.net.user_strings.get(operands[offset].value & 0xFFFFFF).value
        text, key = string_at(text_offset), string_at(key_offset)
        seed = sum(map(ord, key[:4])) & 0xFFFF
        return "".join(chr(ord(char) ^ ((seed + index * 7) % 255)) for index, char in enumerate(text))
    print("\nStatic note filename:", note_literal(2690, 0x89, 0x8E) + note_literal(2691, 0x28, 0x2D))

    for row_id, spans in SECTIONS.items():
        method = tables.MethodDef.rows[row_id - 1]
        print(f"\nMethodDef[{row_id}] token=0x{0x06000000 + row_id:08x} {method.Name}")
        for instruction in body(row_id).instructions:
            if any(start <= instruction.offset < stop for start, stop in spans):
                print(f" body+0x{instruction.offset:04x} {instruction.opcode.name} {token_name(instruction.operand)}")


if __name__ == "__main__":
    main()
