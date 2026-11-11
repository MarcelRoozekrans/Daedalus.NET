// Usage: dotnet run find-reflection-callers.cs -- <publish-dir> <type-substring>
// Lists every assembly with a TypeRef, MemberRef (incl. via TypeSpec generics) or base type
// that mentions a type whose full name contains <type-substring>.
// Heuristic: TypeSpec signatures are not fully parsed. Tokens are decoded by reading any 0x11 or 0x12
// byte as CLASS or VALUETYPE followed by a compressed integer; only 1-byte and 2-byte forms are handled,
// so a stray 0x11 or 0x12 can give a false hit and a 4-byte token can be missed.
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    await Console.Error.WriteLineAsync("Usage: find-reflection-callers.sh <publish-dir> <type-substring>");
    return 2;
}

var dir = args[0];
var needle = args[1];
foreach (var file in Directory.EnumerateFiles(dir, "*.dll").Order(StringComparer.Ordinal))
{
    try
    {
        using var fs = File.OpenRead(file);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) continue;
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) continue;
        var name = Path.GetFileName(file);
        string TypeName(EntityHandle h)
        {
            switch (h.Kind)
            {
                case HandleKind.TypeReference:
                    var tr = md.GetTypeReference((TypeReferenceHandle)h);
                    return $"{md.GetString(tr.Namespace)}.{md.GetString(tr.Name)}";
                case HandleKind.TypeDefinition:
                    var td = md.GetTypeDefinition((TypeDefinitionHandle)h);
                    return $"{md.GetString(td.Namespace)}.{md.GetString(td.Name)}";
                case HandleKind.TypeSpecification:
                    // signature blob: scan for embedded TypeRef/TypeDef tokens
                    var blob = md.GetBlobBytes(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                    return "spec:" + string.Join(',', Tokens(blob).Select(TypeName));
                default: return "?";
            }
        }
        IEnumerable<EntityHandle> Tokens(byte[] b)
        {
            for (int i = 0; i + 1 < b.Length; i++)
            {
                if (b[i] is not (0x11 or 0x12)) continue; // CLASS / VALUETYPE then compressed TypeDefOrRef
                int j = i + 1;
                int v;
                if ((b[j] & 0x80) == 0) v = b[j];
                else if ((b[j] & 0xC0) == 0x80 && j + 1 < b.Length) v = ((b[j] & 0x3F) << 8) | b[j + 1];
                else continue;
                int rid = v >> 2;
                EntityHandle h = (v & 3) switch
                {
                    0 => MetadataTokens.TypeDefinitionHandle(rid),
                    1 => MetadataTokens.TypeReferenceHandle(rid),
                    _ => default
                };
                if (!h.IsNil) yield return h;
            }
        }
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var h in md.TypeReferences)
        {
            var full = TypeName(h);
            if (full.Contains(needle, StringComparison.Ordinal)) hits.Add("TypeRef " + full);
        }
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            var t = TypeName(mr.Parent);
            if (t.Contains(needle, StringComparison.Ordinal)) hits.Add($"MemberRef {t}::{md.GetString(mr.Name)}");
        }
        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            if (!td.BaseType.IsNil && TypeName(td.BaseType).Contains(needle, StringComparison.Ordinal))
                hits.Add($"BaseType of {md.GetString(td.Namespace)}.{md.GetString(td.Name)} : {TypeName(td.BaseType)}");
        }
        if (hits.Count > 0)
        {
            Console.WriteLine($"== {name}");
            foreach (var x in hits) Console.WriteLine("   " + x);
        }
    }
    catch (BadImageFormatException) { await Console.Error.WriteLineAsync($"skip {file}"); }
}

return 0;
