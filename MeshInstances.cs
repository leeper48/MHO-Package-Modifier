using System.Buffers.Binary;
using System.Numerics;

namespace MhoPackageModifier;

/// <summary>
/// --add-mesh-instances: recreates a tile's placements of some meshes in a main level, so real geometry (with its own
/// materials) stands in for the tile at a distance — e.g. Odin's Palace's Bifrost deck instead of grey slabs. For every
/// StaticMeshComponent in the source package whose mesh is one of the named ones, a new component is added to the
/// template's StaticMeshCollectionActor with the source's placement (its own translation / rotation / scale, composed
/// with a standalone owning actor's), the source's materials, an empty lighting record and MinDrawDistance. The mesh
/// and materials must already be in the target under the same paths (copy them first with --copy-export). Same
/// .bak / verify / swap as the other writers.
/// </summary>
static class MeshInstances
{
    static readonly byte[] EmptyLighting = [1, 0, 0, 0, .. new byte[17]];

    /// <summary>
    /// --placed-objects (Midtown's real-geometry LOD test, 2026-11-09): every mesh and component material the tile's visible
    /// placements use, once each, with the package that holds it ("tile", "library", or "?" when it's an import from
    /// somewhere else): what --copy-export must bring into a level before --add-mesh-instances "*".
    /// </summary>
    public static int ListPlaced(string tilePath, string libraryPath)
    {
        var src = Package.Open(tilePath);
        var lib = Package.Open(libraryPath);
        var libPaths = new HashSet<string>(lib.Exports.Select(e => lib.PathOf(e)), StringComparer.OrdinalIgnoreCase);
        string SrcPath(int r)
        {
            if (r > 0) return src.PathOf(src.Exports[r - 1]);
            var parts = new List<string>();
            for (int guard = 0; r < 0 && guard < 16; guard++) { var im = src.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
            if (r > 0) parts.Insert(0, src.PathOf(src.Exports[r - 1]));
            return string.Join('.', parts);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Emit(string kind, int r)
        {
            string p = SrcPath(r);
            if (!seen.Add(p)) return;
            string where = r > 0 ? "tile" : libPaths.Contains(p) ? "library" : "?";
            Console.WriteLine($"{kind}\t{where}\t{p}");
        }
        foreach (var e in src.Exports)
        {
            if (!src.ClassOf(e).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = src.ReadExportBytes(e);
            var c = ComponentTransform.Read(src, d);
            if (c == null || c.MeshRef == 0 || c.HiddenGame) continue;
            Emit("mesh", c.MeshRef);
            var tags = TagWalker.Walk(src, d, 8);
            var mt = tags?.FirstOrDefault(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
            if (mt == null) continue;
            int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mt.ValueAt));
            for (int k = 0; k < n; k++) { int mr = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mt.ValueAt + 4 + 4 * k)); if (mr != 0) Emit("material", mr); }
        }
        return 0;
    }

    public static int Run(string upkPath, string srcPath, IReadOnlyList<string> meshNames, string templatePath, float minDraw, float zOffset, bool dryRun, IReadOnlyList<(string From, string To)>? swap = null, float inset = 1f, bool skipMissing = false, bool lit = false,
        string? libraryPath = null)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        var src = Package.Open(srcPath);
        Console.WriteLine($"Mesh instances: {string.Join(", ", meshNames)} from {Path.GetFileName(srcPath)} -> {Path.GetFileName(upkPath)}, MinDrawDistance {minDraw}{(zOffset != 0 ? $", z {zOffset:+0;-0}" : "")}{(dryRun ? "  [dry run]" : "")}");
        int template = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(templatePath, StringComparison.OrdinalIgnoreCase));
        if (template < 0 || !pkg.ClassOf(pkg.Exports[template]).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"  no template StaticMeshComponent '{templatePath}'"); return 2; }
        int actor = pkg.Exports[template].OuterIndex - 1;
        if (actor < 0 || !pkg.ClassOf(pkg.Exports[actor]).Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  the template isn't in a StaticMeshCollectionActor"); return 1; }

        // Target exports by path (the copied mesh and materials).
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pkg.Exports.Length; i++) byPath.TryAdd(pkg.PathOf(pkg.Exports[i]), i + 1);
        string SrcPath(int r)
        {
            if (r > 0) return src.PathOf(src.Exports[r - 1]);
            var parts = new List<string>();
            for (int guard = 0; r < 0 && guard < 16; guard++) { var im = src.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
            if (r > 0) parts.Insert(0, src.PathOf(src.Exports[r - 1]));
            return string.Join('.', parts);
        }

        var names = pkg.Names.Any(n => n.Equals("MinDrawDistance", StringComparison.OrdinalIgnoreCase)) ? new List<string>() : ["MinDrawDistance"];
        var tw = new TagWriter(pkg, names);

        // --library <lib.upk> (2026-11-11, the Shipping Yard's tile-carried LOD): a mesh or material that isn't in the target but is
        // in the region library is imported by its path, as the tiles import the library's own meshes (a chain of Package imports,
        // then the object). The library is loaded before the tiles, so the import resolves.
        var addImports = new List<NewImport>();
        Package? lib = libraryPath != null ? Package.Open(libraryPath) : null;
        var libByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (lib != null) for (int i = 0; i < lib.Exports.Length; i++) libByPath.TryAdd(lib.PathOf(lib.Exports[i]), i + 1);
        string TargetImportPath(int r)
        {
            var parts = new List<string>();
            for (int guard = 0; r < 0 && guard < 16; guard++) { var im = pkg.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
            return r > 0 ? pkg.PathOf(pkg.Exports[r - 1]) + "." + string.Join('.', parts) : string.Join('.', parts);
        }
        var importByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pkg.Imports.Length; i++) importByPath.TryAdd(TargetImportPath(-(i + 1)), -(i + 1));
        void EnsureName(string n) { if (!pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) && !names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))) names.Add(n); }
        // a name stored with a number (x_3 = "x", number 4) unless the library has the whole text as a name
        (string Name, int Number) Split(string objectName)
        {
            int us = objectName.LastIndexOf('_');
            if (lib != null && us > 0 && int.TryParse(objectName[(us + 1)..], out int n) && n >= 0 && objectName[(us + 1)..] == n.ToString()
                && !lib.Names.Any(x => x.Equals(objectName, StringComparison.OrdinalIgnoreCase)))
                return (objectName[..us], n + 1);
            return (objectName, 0);
        }
        int ImportFromLibrary(string path)
        {
            if (importByPath.TryGetValue(path, out int have)) return have;
            int li = libByPath[path];
            var e = lib!.Exports[li - 1];
            int outer = 0;
            if (e.OuterIndex > 0) outer = ImportFromLibrary(lib.PathOf(lib.Exports[e.OuterIndex - 1]));
            string cls = lib.ClassOf(e);
            string clsPkg = e.ClassIndex < 0 && lib.Imports[-e.ClassIndex - 1].OuterIndex < 0 ? lib.Imports[-lib.Imports[-e.ClassIndex - 1].OuterIndex - 1].ObjectName
                : cls.Equals("Package", StringComparison.OrdinalIgnoreCase) ? "Core" : "Engine";
            var (name, number) = Split(e.ObjectName);
            foreach (string n in new[] { clsPkg, cls, name }) EnsureName(n);
            addImports.Add(new NewImport(clsPkg, cls, outer, name, number));
            int r = -(pkg.Imports.Length + addImports.Count);
            importByPath[path] = r;
            Console.WriteLine($"  import {cls} {path} (from {Path.GetFileName(libraryPath)})");
            return r;
        }
        bool Find(string path, out int r)
        {
            if (byPath.TryGetValue(path, out r)) return true;
            if (lib != null && libByPath.ContainsKey(path)) { r = ImportFromLibrary(path); return true; }
            return false;
        }
        byte[] tsrc = pkg.ReadExportBytes(pkg.Exports[template]);
        var ttags = TagWalker.Walk(pkg, tsrc, 8) ?? throw new InvalidDataException("template component properties don't parse");

        var add = new List<NewExport>();
        var insetCentre = new Dictionary<int, Vector3?>();
        var refs = new List<int>();
        var expect = new List<(int Mesh, Vector3 T, Vector3 R, Vector3 S)>();
        foreach (var e in src.Exports)
        {
            if (!src.ClassOf(e).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = src.ReadExportBytes(e);
            var c = ComponentTransform.Read(src, d);
            if (c == null || c.MeshRef == 0 || c.HiddenGame) continue;
            string meshName = src.RefName(c.MeshRef);
            // "*" = every placed mesh (Midtown's real-geometry LOD test, 2026-11-09)
            if (!meshNames.Any(m => m == "*" || m.Equals(meshName, StringComparison.OrdinalIgnoreCase))) continue;
            Vector3 t = c.Translation, r = c.Rotation, s = c.Scale;
            if (e.OuterIndex > 0 && src.ClassOf(src.Exports[e.OuterIndex - 1]) is string oc && oc.EndsWith("Actor", StringComparison.OrdinalIgnoreCase)
                && !oc.Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)
                && ComponentTransform.ReadActor(src, src.ReadExportBytes(src.Exports[e.OuterIndex - 1])) is { } a)
            {
                t = a.Translation + Vector3.Transform(t * a.Scale, ZonePlaceholders.RotatorMatrix(a.Rotation));
                r = a.Rotation + r; s = a.Scale * s;
            }
            t.Z += zOffset;
            string meshPath = SrcPath(c.MeshRef);
            // --material from=to swaps meshes too (the Shipping Yard's LOD meshes renamed in the library, 2026-11-11)
            if (swap?.FirstOrDefault(x => x.From.Equals(meshPath, StringComparison.OrdinalIgnoreCase)) is { To: { } meshTo }) meshPath = meshTo;
            if (!Find(meshPath, out int meshRef))
            {
                // --skip-missing: a placement whose mesh or material wasn't copied is left out instead of stopping (Midtown's real-geometry test)
                if (skipMissing) { Console.WriteLine($"  skipped: mesh {meshPath} isn't in the target"); continue; }
                Console.WriteLine($"  mesh {meshPath} isn't in the target (copy it first with --copy-export)"); return 1;
            }
            // --inset F: scaled by F about the mesh's own bounds centre, so the copy sits just inside the real piece
            if (inset != 1f)
            {
                if (!insetCentre.TryGetValue(meshRef, out var ctr))
                {
                    // a mesh the reader can't parse keeps its size (Midtown: one Bank tile piece)
                    try { var sm = StaticMesh.Read(pkg, pkg.Exports[meshRef - 1]); ctr = (sm.Positions.Aggregate(Vector3.Min) + sm.Positions.Aggregate(Vector3.Max)) / 2; }
                    catch (PackageFormatException) { ctr = null; Console.WriteLine($"  {meshName}: not read, kept at full size"); }
                    insetCentre[meshRef] = ctr;
                }
                if (ctr is Vector3 cc)
                {
                    t += Vector3.Transform(cc * s, ZonePlaceholders.RotatorMatrix(r)) * (1 - inset);
                    s *= inset;
                }
            }
            // Materials: the source component's list, each found in the target by path.
            var stags = TagWalker.Walk(src, d, 8);
            var mtag = stags?.FirstOrDefault(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
            var mats = new List<int>();
            bool missing = false;
            if (mtag != null)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mtag.ValueAt));
                for (int k = 0; k < n; k++)
                {
                    int mr = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mtag.ValueAt + 4 + 4 * k));
                    if (mr == 0) { mats.Add(0); continue; }
                    string mp = SrcPath(mr);
                    // --material from=to: the copy uses another material (Odin's Palace: the live rainbow instead of the dead one).
                    if (swap?.FirstOrDefault(x => x.From.Equals(mp, StringComparison.OrdinalIgnoreCase)) is { To: { } to }) mp = to;
                    if (!Find(mp, out int dm))
                    {
                        if (skipMissing) { Console.WriteLine($"  skipped a {meshName}: material {mp} isn't in the target"); missing = true; break; }
                        Console.WriteLine($"  material {mp} isn't in the target (copy it first with --copy-export)"); return 1;
                    }
                    mats.Add(dm);
                }
            }

            if (missing) continue;
            using var ms = new MemoryStream();
            ms.Write(tsrc, 0, 8);
            foreach (var tg in ttags)
            {
                switch (tg.Name.ToLowerInvariant())
                {
                    case "staticmesh": ms.Write(tw.Tag("StaticMesh", "ObjectProperty", null, BitConverter.GetBytes(meshRef))); break;
                    case "materials": if (mats.Count > 0) ms.Write(tw.Tag("Materials", "ArrayProperty", null, [.. BitConverter.GetBytes(mats.Count), .. mats.SelectMany(BitConverter.GetBytes)])); break;
                    case "translation" or "rotation" or "scale3d" or "scale" or "visibilityid" or "vertexpositionversionnumber" or "mindrawdistance": break;
                    // --lit (Midtown's real-geometry test): the sky template's "no dynamic lights" left out, so the copies (no
                    // lightmap of their own) take the zone's sun as dynamic light, as characters do
                    case "bacceptsdynamiclights" or "bacceptsdynamicdominantlightshadows" when lit: break;
                    default: ms.Write(tsrc, tg.Start, tg.End - tg.Start); break;
                }
            }
            if (mats.Count > 0 && !ttags.Any(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase)))
                ms.Write(tw.Tag("Materials", "ArrayProperty", null, [.. BitConverter.GetBytes(mats.Count), .. mats.SelectMany(BitConverter.GetBytes)]));
            ms.Write(tw.Tag("Translation", "StructProperty", "Vector", [.. BitConverter.GetBytes(t.X), .. BitConverter.GetBytes(t.Y), .. BitConverter.GetBytes(t.Z)]));
            ms.Write(tw.Tag("Rotation", "StructProperty", "Rotator", [.. BitConverter.GetBytes((int)r.X), .. BitConverter.GetBytes((int)r.Y), .. BitConverter.GetBytes((int)r.Z)]));
            if (s != Vector3.One) ms.Write(tw.Tag("Scale3D", "StructProperty", "Vector", [.. BitConverter.GetBytes(s.X), .. BitConverter.GetBytes(s.Y), .. BitConverter.GetBytes(s.Z)]));
            ms.Write(tw.Tag("MinDrawDistance", "FloatProperty", null, BitConverter.GetBytes(minDraw)));
            ms.Write(tsrc, ttags.NoneAt, 8);
            ms.Write(EmptyLighting);
            byte[] bytes = ms.ToArray();
            refs.Add(pkg.Exports.Length + add.Count + 1);
            add.Add(new NewExport(template, CellPlaceholders.NextNumber(pkg, template, add.Count), _ => bytes));
            expect.Add((meshRef, t, new Vector3((int)r.X, (int)r.Y, (int)r.Z), s));
            Console.WriteLine($"  {meshName}: at ({t.X:0}, {t.Y:0}, {t.Z:0}), yaw {r.Y * 360f / 65536f:0.#}, scale ({s.X:0.##}, {s.Y:0.##}, {s.Z:0.##}), {mats.Count} material(s)");
        }
        if (add.Count == 0) { Console.WriteLine("  no placements of those meshes in the source"); return 1; }

        byte[] actorBytes = CellPlaceholders.BuildActor(pkg, actor, tw, refs);
        var replace = new Dictionary<int, Func<long, byte[]>> { [actor] = _ => actorBytes };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, add, out var written, names, addImports);
        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [actor], add, written, names, addImports);
            var w = Package.FromBytes(bytes);
            for (int k = 0; k < refs.Count; k++)
            {
                var c = ComponentTransform.Read(w, w.ReadExportBytes(w.Exports[refs[k] - 1]));
                if (c == null || c.MeshRef != expect[k].Mesh || Vector3.Distance(c.Translation, expect[k].T) > 0.01f || c.Rotation != expect[k].R || Vector3.Distance(c.Scale, expect[k].S) > 1e-4f)
                    problems.Add($"instance {k + 1} doesn't read back with its mesh and placement");
            }
            return problems;
        }
        var problems = Check(output);
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS ({refs.Count} instance(s) read back with their mesh and placement; actor lists them; every other export identical)");
        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(upkPath));
            File.WriteAllBytes(target, output);
            Console.WriteLine($"  dry run: wrote {target} (game folder untouched)");
            return 0;
        }
        return MeshImport.WriteLive(upkPath, output, Check) ? 0 : 1;
    }
}
