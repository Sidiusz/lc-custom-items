using Mono.Cecil; using Mono.Cecil.Cil; using System; using System.IO; using System.Linq; using System.Collections.Generic;
var refDir = args[0];
var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(refDir);
foreach (var d in args.Skip(2)) resolver.AddSearchDirectory(d);
var found = new SortedSet<string>();
bool NonPublic(ICustomAttributeProvider p, out string orig) {
  orig = null;
  var a = p.CustomAttributes.FirstOrDefault(c => c.AttributeType.Name.StartsWith("OriginalAttributes"));
  if (a == null) return false;
  var v = BitConverter.ToInt32(a.GetBlob(), 2);
  // Field/Method access mask 7: Public = 6
  if ((v & 7) == 6) return false;
  orig = "access=" + (v & 7); return true;
}
bool TypeNonPublic(TypeDefinition t) {
  var a = t.CustomAttributes.FirstOrDefault(c => c.AttributeType.Name.StartsWith("OriginalAttributes"));
  if (a == null) return false; var v = BitConverter.ToInt32(a.GetBlob(), 2);
  var vis = v & 7; return !(vis == 1 || vis == 2);
}
foreach (var path in args[1].Split(',')) {
  var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver });
  foreach (var type in asm.MainModule.GetTypes()) foreach (var m in type.Methods.Where(m => m.HasBody)) {
    int line = 0;
    foreach (var ins in m.Body.Instructions) {
      IMemberDefinition def = null; string what = null;
      try {
        if (ins.Operand is MethodReference mr) { def = mr.Resolve(); what = mr.DeclaringType.Name + "." + mr.Name; }
        else if (ins.Operand is FieldReference fr) { def = fr.Resolve(); what = fr.DeclaringType.Name + "." + fr.Name; }
        else if (ins.Operand is TypeReference tr) { var td = tr.Resolve(); if (td != null && td.Module.Assembly != asm && TypeNonPublic(td)) found.Add($"TYPE {td.FullName} in {type.Name}.{m.Name}"); }
      } catch (Exception e) { found.Add("ERR " + e.GetType().Name + " " + e.Message.Split((char)10)[0]); }
      if (def == null || def.DeclaringType.Module.Assembly == asm) continue;
      if (NonPublic((ICustomAttributeProvider)def, out var o) || TypeNonPublic(def.DeclaringType))
        found.Add($"{what} [{o}] in {Path.GetFileNameWithoutExtension(path)}:{type.FullName}.{m.Name}");
    }
  }
}
foreach (var f in found) Console.WriteLine(f);
