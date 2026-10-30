"""Inspect assembly_valheim.dll without a decompiler: list members or disassemble methods.

Setup:  pip install dnfile dncil
Usage:  python tools/valheim_il.py members <assembly_valheim.dll> [filter]
        python tools/valheim_il.py il <assembly_valheim.dll> Type::Method [Type::Method ...]

Use it to check that every Harmony target in src/Patches.cs still exists after a game update,
and to read what a vanilla method does.
"""
import sys
import dnfile
from dncil.cil.body import CilMethodBody
from dncil.cil.body.reader import CilMethodBodyReaderBase
from dncil.clr.token import Token, StringToken


class _Reader(CilMethodBodyReaderBase):
    def __init__(self, pe, row):
        self.pe = pe
        self.off = pe.get_offset_from_rva(row.Rva)

    def read(self, n):
        data = self.pe.get_data(self.pe.get_rva_from_offset(self.off), n)
        self.off += n
        return data

    def tell(self):
        return self.off

    def seek(self, off):
        self.off = off
        return off


def _owners(md):
    methods, fields = {}, {}
    for t in md.TypeDef.rows:
        name = (str(t.TypeNamespace) + "." if str(t.TypeNamespace) else "") + str(t.TypeName)
        for m in t.MethodList or []:
            methods[m.row_index] = name
        for f in t.FieldList or []:
            fields[f.row_index] = name
    return methods, fields


def members(path, flt=None):
    pe = dnfile.dnPE(path)
    md = pe.net.mdtables
    methods, fields = _owners(md)
    for i, m in enumerate(md.MethodDef.rows, 1):
        f = m.Flags
        line = "M {}::{}({}) {}{}".format(
            methods.get(i, "?"), m.Name, ", ".join(str(p.row.Name) for p in (m.ParamList or [])),
            "public" if f.mdPublic else "nonpublic", " static" if f.mdStatic else "")
        if not flt or flt in line:
            print(line)
    for i, fl in enumerate(md.Field.rows, 1):
        line = "F {}::{} {}{}".format(fields.get(i, "?"), fl.Name,
                                      "public" if fl.Flags.fdPublic else "nonpublic",
                                      " static" if fl.Flags.fdStatic else "")
        if not flt or flt in line:
            print(line)


def il(path, targets):
    pe = dnfile.dnPE(path)
    md = pe.net.mdtables
    methods, fields = _owners(md)

    def name(tok):
        try:
            if tok.table == 0x0A:
                m = md.MemberRef.rows[tok.rid - 1]
                cls = getattr(m.Class.row, "TypeName", "?")
                return f"{cls}::{m.Name}"
            if tok.table == 0x06:
                return f"{methods.get(tok.rid, '?')}::{md.MethodDef.rows[tok.rid - 1].Name}"
            if tok.table == 0x04:
                return f"{fields.get(tok.rid, '?')}::{md.Field.rows[tok.rid - 1].Name}"
        except Exception:
            pass
        return str(tok)

    for i, m in enumerate(md.MethodDef.rows, 1):
        full = f"{methods.get(i)}::{m.Name}"
        if full not in targets or not m.Rva:
            continue
        print("=====", full)
        for ins in CilMethodBody(_Reader(pe, m)).instructions:
            op = ins.operand
            if isinstance(op, StringToken):
                try:
                    op = repr(pe.net.user_strings.get(op.rid).value)
                except Exception:
                    pass
            elif isinstance(op, Token):
                op = name(op)
            print(f"  {ins.opcode} {op if op is not None else ''}")


if __name__ == "__main__":
    if len(sys.argv) < 3 or sys.argv[1] not in ("members", "il"):
        print(__doc__)
        sys.exit(1)
    if sys.argv[1] == "members":
        members(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
    else:
        il(sys.argv[2], set(sys.argv[3:]))
