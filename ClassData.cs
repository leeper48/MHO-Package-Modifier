using System.Buffers.Binary;
using System.Text;

namespace MhoPackageModifier;

/// <summary>
/// A compiled script class's export data (UClass, as this game's packages store it; 2.53.28). Worked out from the client's own
/// UStruct / UState readers (MHO-Client-Decompile, read only: UState's ProbeMask is 4 bytes here, not UE3's 8) and checked
/// byte for byte on marvelprojectile_punisherstungrenade (198 bytes). PC-cooked packages keep the editor fields (ScriptText,
/// CppText, Line / TextPos, the category lists, the header file name).
/// Layout: NetIndex; UField Next; UStruct SuperStruct, ScriptText, Children, CppText, Line, TextPos, bytecode size, storage
/// size, bytecode; UState ProbeMask (4), LabelTableOffset (2), StateFlags, FuncMap (name → function); UClass ClassFlags,
/// ClassWithin, ClassConfigName, ComponentNameToDefaultObjectMap (name → component), Interfaces (class, pointer property),
/// DontSortCategories, HideCategories, AutoExpandCategories, AutoCollapseCategories (names), bForceScriptOrder,
/// ClassGroupNames (names), ClassHeaderFilename (FString), DLLBindName, ClassDefaultObject.
/// </summary>
public sealed class ClassData
{
    /// <summary>Byte offsets of every object reference (4 bytes) and name reference (8 bytes) in the data.</summary>
    public readonly List<int> ObjectRefs = [], NameRefs = [];
    public int ScriptSize, ScriptStorage, Children, Functions, Interfaces, Components;
    public int End;

    /// <summary>The class data parsed, or null with the reason when it doesn't read to its end.</summary>
    public static ClassData? Parse(byte[] d, out string? error)
    {
        error = null;
        var c = new ClassData();
        int p = 0;
        int I() { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }
        void Obj() { c.ObjectRefs.Add(p); p += 4; }
        void Name() { c.NameRefs.Add(p); p += 8; }
        void Names() { int n = I(); if (n < 0 || n > 10000) throw new InvalidDataException("name list count " + n); for (int k = 0; k < n; k++) Name(); }
        try
        {
            p += 4;              // NetIndex
            Obj();               // Next
            Obj();               // SuperStruct
            Obj();               // ScriptText
            c.Children = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); Obj();   // Children
            Obj();               // CppText
            p += 8;              // Line, TextPos
            c.ScriptSize = I();
            c.ScriptStorage = I();
            if (c.ScriptStorage < 0 || p + c.ScriptStorage > d.Length) throw new InvalidDataException("script storage " + c.ScriptStorage);
            p += c.ScriptStorage;
            p += 4 + 2 + 4;      // ProbeMask, LabelTableOffset, StateFlags
            int fn = I();
            if (fn < 0 || fn > 10000) throw new InvalidDataException("function map count " + fn);
            c.Functions = fn;
            for (int k = 0; k < fn; k++) { Name(); Obj(); }
            p += 4;              // ClassFlags
            Obj();               // ClassWithin
            Name();              // ClassConfigName
            int cm = I();
            if (cm < 0 || cm > 10000) throw new InvalidDataException("component map count " + cm);
            c.Components = cm;
            for (int k = 0; k < cm; k++) { Name(); Obj(); }
            int ifc = I();
            if (ifc < 0 || ifc > 1000) throw new InvalidDataException("interface count " + ifc);
            c.Interfaces = ifc;
            for (int k = 0; k < ifc; k++) { Obj(); Obj(); }
            Names(); Names(); Names(); Names();   // DontSort, Hide, AutoExpand, AutoCollapse categories
            p += 4;              // bForceScriptOrder
            Names();             // ClassGroupNames
            int len = I();       // ClassHeaderFilename (FString: length, negative = UTF-16)
            p += len >= 0 ? len : -len * 2;
            Name();              // DLLBindName
            Obj();               // ClassDefaultObject
            c.End = p;
            if (p != d.Length) { error = $"read {p} of {d.Length} bytes"; return null; }
            return c;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException or IndexOutOfRangeException)
        {
            error = ex.Message + $" (at {p} of {d.Length})";
            return null;
        }
    }
}
