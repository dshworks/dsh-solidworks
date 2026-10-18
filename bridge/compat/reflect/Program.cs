using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// usage: reflect <SolidWorks.Interop.sldworks.dll> <Interface>...
// prints Interface<TAB>Member for every method and property of the named interfaces, sorted.
// Reads metadata only: no COM, no SOLIDWORKS, any OS.
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();
var want = new HashSet<string>(args.Skip(1));
var rows = new SortedSet<string>(StringComparer.Ordinal);
foreach (var handle in md.TypeDefinitions)
{
    var type = md.GetTypeDefinition(handle);
    var name = md.GetString(type.Name);
    if (!want.Contains(name)) continue;
    foreach (var m in type.GetMethods())
    {
        var member = md.GetString(md.GetMethodDefinition(m).Name);
        if (member.StartsWith("get_") || member.StartsWith("set_") || member.StartsWith("let_")) member = member.Substring(4);
        rows.Add($"{name}\t{member}");
    }
    foreach (var p in type.GetProperties()) rows.Add($"{name}\t{md.GetString(md.GetPropertyDefinition(p).Name)}");
}
foreach (var r in rows) Console.WriteLine(r);
