#!/usr/bin/env python3
"""Verify every type an exe references exists in the .NET Framework assembly it names.
Needs: pip install dnfile, and Mono's 4.8-api reference assemblies (real .NET Framework surface).
usage: check_typerefs.py <exe> [refdir]"""
import sys, os, dnfile

exe_path = sys.argv[1]
refdir = sys.argv[2] if len(sys.argv) > 2 else "/usr/lib/mono/4.8-api"
cache = {}

def defs(asm):
    if asm in cache: return cache[asm]
    path = os.path.join(refdir, asm + ".dll")
    s = set()
    if os.path.exists(path):
        pe = dnfile.dnPE(path)
        t = pe.net.mdtables
        for td in t.TypeDef: s.add((str(td.TypeNamespace), str(td.TypeName)))
        if t.ExportedType:
            for et in t.ExportedType: s.add((str(et.TypeNamespace), str(et.TypeName)))
        cache[asm] = s
    else:
        cache[asm] = None
    return cache[asm]

pe = dnfile.dnPE(exe_path)
t = pe.net.mdtables
bad = []
checked = 0
for tr in t.TypeRef:
    scope = tr.ResolutionScope
    if scope is None or scope.table is None: continue
    if scope.table.name != "AssemblyRef": continue    # nested types resolve through their parent
    asm = str(scope.row.Name)
    ns, name = str(tr.TypeNamespace), str(tr.TypeName)
    d = defs(asm)
    checked += 1
    if d is None:
        bad.append((asm, ns, name, "assembly not in reference set"))
    elif (ns, name) not in d:
        bad.append((asm, ns, name, "type not in that assembly"))
for b in bad: print("MISSING: [%s] %s.%s  (%s)" % b)
print("%d type references checked, %d problems" % (checked, len(bad)))
sys.exit(1 if bad else 0)
