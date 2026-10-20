using System.Buffers.Binary;

namespace MhoPackageModifier;

/// <summary>
/// Compiled UnrealScript in this game's packages (2.53.30): the function and property exports a script class carries
/// (powerfxhit's oninit, its float and bool properties …), read so a class with code can be copied into another package with
/// every reference remapped. From the UE3 reference (ScriptSerialization.h: UStruct::SerializeExpr; UnProp.cpp: the property
/// layouts; UnClass.cpp: UFunction::Serialize) checked byte for byte on this game's files:
/// <list type="bullet">
/// <item>Fields begin with the object's tagged properties: NetIndex, then None (8 bytes) for an empty list.</item>
/// <item>Property: Next, ArrayDim, PropertyFlags (8), Category (name), ArraySizeEnum (object); RepOffset (2) when CPF_Net; then the
/// type's own references (struct, object class, enum, inner …). No bool bitmask in this build.</item>
/// <item>Function: the struct part (as ClassData: SuperStruct, ScriptText, Children, CppText, Line, TextPos, code size in memory,
/// code size on disk, the code), iNative (2), OperPrecedence (1), FunctionFlags (4), RepOffset (2) when FUNC_Net, FriendlyName.</item>
/// <item>Code on disk: object references 4 bytes (8 in this 64-bit client's memory: so the two sizes differ), names 8; jump
/// offsets count memory positions and need no change when references are remapped.</item>
/// </list>
/// </summary>
public static class ScriptCode
{
    /// <summary>References (object 4 bytes, name 8) found in a field export's data, by byte offset.</summary>
    public sealed class Refs
    {
        public readonly List<int> Objects = [], Names = [];
        public int Tokens;
    }

    static int I32(byte[] d, int p) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));

    /// <summary>
    /// A property export (class name like "floatproperty"): its references, or null with the reason when it doesn't read to
    /// its end. <paramref name="cls"/> picks the type's own references.
    /// </summary>
    public static Refs? Property(byte[] d, string cls, out string? error)
    {
        error = null;
        var r = new Refs();
        int p = 0;
        try
        {
            p = 4;                                  // NetIndex
            if (I32(d, 4 + 4) != 0) { error = "tagged properties aren't the empty list"; return null; }
            r.Names.Add(p); p += 8;                 // None (the empty tagged property list)
            r.Objects.Add(p); p += 4;               // Next
            p += 4;                                 // ArrayDim
            long flags = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(p)); p += 8;
            r.Names.Add(p); p += 8;                 // Category
            r.Objects.Add(p); p += 4;               // ArraySizeEnum
            if ((flags & 0x20) != 0) p += 2;        // CPF_Net: RepOffset
            switch (cls.ToLowerInvariant())
            {
                case "byteproperty": case "structproperty": case "objectproperty": case "componentproperty":
                case "arrayproperty": case "interfaceproperty":
                    r.Objects.Add(p); p += 4; break;   // Enum / Struct / PropertyClass / Inner / InterfaceClass
                case "classproperty": r.Objects.Add(p); p += 4; r.Objects.Add(p); p += 4; break;   // PropertyClass, MetaClass
                case "delegateproperty": r.Objects.Add(p); p += 4; r.Objects.Add(p); p += 4; break; // Function, SourceDelegate
                case "mapproperty": r.Objects.Add(p); p += 4; r.Objects.Add(p); p += 4; break;      // Key, Value
                case "boolproperty": case "intproperty": case "floatproperty": case "nameproperty": case "strproperty": break;
                default: error = "property class " + cls + " not known"; return null;
            }
            if (p != d.Length) { error = $"read {p} of {d.Length} bytes"; return null; }
            return r;
        }
        catch (ArgumentOutOfRangeException) { error = $"read past the end (at {p} of {d.Length})"; return null; }
    }

    /// <summary>A function export: its references (struct part, code, friendly name), or null with the reason.</summary>
    public static Refs? Function(byte[] d, out string? error)
    {
        error = null;
        var r = new Refs();
        int p = 0;
        try
        {
            p = 4;
            if (I32(d, 4 + 4) != 0) { error = "tagged properties aren't the empty list"; return null; }
            r.Names.Add(p); p += 8;                 // None
            r.Objects.Add(p); p += 4;               // Next
            r.Objects.Add(p); p += 4;               // SuperStruct
            r.Objects.Add(p); p += 4;               // ScriptText
            r.Objects.Add(p); p += 4;               // Children
            r.Objects.Add(p); p += 4;               // CppText
            p += 8;                                 // Line, TextPos
            p += 4;                                 // code size in memory
            int storage = I32(d, p); p += 4;
            if (storage < 0 || p + storage > d.Length) { error = "code size " + storage; return null; }
            if (!Walk(d, p, p + storage, r, out string? why)) { error = why; return null; }
            p += storage;
            p += 2 + 1;                             // iNative, OperPrecedence
            int fflags = I32(d, p); p += 4;
            if ((fflags & 0x40) != 0) p += 2;       // FUNC_Net: RepOffset
            r.Names.Add(p); p += 8;                 // FriendlyName
            if (p != d.Length) { error = $"read {p} of {d.Length} bytes"; return null; }
            return r;
        }
        catch (ArgumentOutOfRangeException) { error = $"read past the end (at {p} of {d.Length})"; return null; }
    }

    /// <summary>Walks compiled code between <paramref name="start"/> and <paramref name="end"/> (disk form), adding the offsets
    /// of every object reference and name; false with the reason when a token isn't known or it doesn't end exactly.</summary>
    public static bool Walk(byte[] d, int start, int end, Refs r, out string? error)
    {
        error = null;
        int p = start;
        try
        {
            while (p < end) Expr(d, ref p, end, r);
            if (p != end) { error = $"code read to {p - start} of {end - start} bytes"; return false; }
            return true;
        }
        catch (InvalidDataException ex) { error = ex.Message + $" (at code byte {p - start})"; return false; }
        catch (ArgumentOutOfRangeException) { error = $"code read past its end (at code byte {p - start})"; return false; }
    }

    const byte EndFunctionParms = 0x16, DebugInfo = 0x41;

    static void Obj(ref int p, Refs r) { r.Objects.Add(p); p += 4; }
    static void Name(ref int p, Refs r) { r.Names.Add(p); p += 8; }

    /// <summary>EX_DebugInfo with version 100 following a call (HANDLE_OPTIONAL_DEBUG_INFO); cooked code seldom has it.</summary>
    static void OptionalDebug(byte[] d, ref int p, int end, Refs r)
    {
        if (p < end && d[p] == DebugInfo && p + 5 <= end && I32(d, p + 1) == 100) Expr(d, ref p, end, r);
    }

    static void Params(byte[] d, ref int p, int end, Refs r)
    {
        while (Expr(d, ref p, end, r) != EndFunctionParms) { if (p >= end) throw new InvalidDataException("function parameters run past the end"); }
    }

    /// <summary>One expression (UStruct::SerializeExpr); returns its token.</summary>
    static byte Expr(byte[] d, ref int p, int end, Refs r)
    {
        if (p >= end) throw new InvalidDataException("expression past the end");
        byte t = d[p++];
        r.Tokens++;
        if (t >= 0x70) { Params(d, ref p, end, r); OptionalDebug(d, ref p, end, r); return t; }        // a native function by index
        if (t >= 0x60) { p++; Params(d, ref p, end, r); OptionalDebug(d, ref p, end, r); return t; }   // extended native (one more index byte)
        switch (t)
        {
            case 0x38: p++; Expr(d, ref p, end, r); break;                                          // PrimitiveCast: kind, expr
            case 0x52: Obj(ref p, r); Expr(d, ref p, end, r); break;                                // InterfaceCast
            case 0x0F: case 0x14: case 0x44: Expr(d, ref p, end, r); Expr(d, ref p, end, r); break; // Let, LetBool, LetDelegate
            case 0x06: p += 2; break;                                                               // Jump
            case 0x00: case 0x01: case 0x02: case 0x48: case 0x03: Obj(ref p, r); break;            // Local/Instance/Default/LocalOut/State variable
            case 0x41: p += 4 + 4 + 4 + 1; break;                                                   // DebugInfo
            case 0x2D: case 0x51: Expr(d, ref p, end, r); break;                                    // BoolVariable, InterfaceContext
            case 0x0B: case 0x53: case 0x16: case 0x4A: case 0x25: case 0x26: case 0x27: case 0x28:
            case 0x2A: case 0x3F: case 0x17: case 0x30: case 0x08: case 0x31: case 0x15: break;     // no operands
            case 0x3A: case 0x0E: Obj(ref p, r); break;                                              // ReturnNothing, EatReturnValue
            case 0x04: Expr(d, ref p, end, r); break;                                                // Return
            case 0x1C: Obj(ref p, r); Params(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // FinalFunction
            case 0x1B: case 0x37: Name(ref p, r); Params(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // Virtual / Global function
            case 0x42: p++; Obj(ref p, r); Name(ref p, r); break;                                    // DelegateFunction
            case 0x29: Obj(ref p, r); break;                                                          // NativeParm
            case 0x12: case 0x19: Expr(d, ref p, end, r); p += 2; Obj(ref p, r); p++; Expr(d, ref p, end, r); break;   // ClassContext, Context
            case 0x58: Expr(d, ref p, end, r); Expr(d, ref p, end, r); p++; Expr(d, ref p, end, r); p += 2; break;    // DynArrayIterator
            case 0x1A: case 0x10: Expr(d, ref p, end, r); Expr(d, ref p, end, r); break;             // ArrayElement, DynArrayElement
            case 0x36: Expr(d, ref p, end, r); break;                                                 // DynArrayLength
            case 0x54: Expr(d, ref p, end, r); Expr(d, ref p, end, r); Expr(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // DynArrayAdd
            case 0x39: case 0x40: for (int k = 0; k < 4; k++) Expr(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // DynArrayInsert / Remove
            case 0x55: case 0x56: Expr(d, ref p, end, r); p += 2; Expr(d, ref p, end, r); Expr(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // Add/RemoveItem
            case 0x57: Expr(d, ref p, end, r); p += 2; Expr(d, ref p, end, r); Expr(d, ref p, end, r); Expr(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // InsertItem
            case 0x46: Expr(d, ref p, end, r); p += 2; Expr(d, ref p, end, r); Expr(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // DynArrayFind
            case 0x47: Expr(d, ref p, end, r); p += 2; Expr(d, ref p, end, r); Expr(d, ref p, end, r); Expr(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // FindStruct
            case 0x59: Expr(d, ref p, end, r); p += 2; Expr(d, ref p, end, r); Expr(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // DynArraySort
            case 0x45: Expr(d, ref p, end, r); p += 2; Expr(d, ref p, end, r); p += 2; Expr(d, ref p, end, r); break;   // Conditional
            case 0x11: for (int k = 0; k < 4; k++) Expr(d, ref p, end, r); break;                     // New
            case 0x1D: p += 4; break;                                                                  // IntConst
            case 0x1E: p += 4; break;                                                                  // FloatConst
            case 0x1F: while (d[p++] != 0) { if (p >= end) throw new InvalidDataException("string past the end"); } break;   // StringConst
            case 0x34: while (true) { int c = d[p] | d[p + 1] << 8; p += 2; if (c == 0) break; if (p >= end) throw new InvalidDataException("string past the end"); } break;
            case 0x20: Obj(ref p, r); break;                                                           // ObjectConst
            case 0x21: Name(ref p, r); break;                                                          // NameConst
            case 0x22: p += 12; break;                                                                 // RotationConst
            case 0x23: p += 12; break;                                                                 // VectorConst
            case 0x24: case 0x2C: p++; break;                                                          // ByteConst, IntConstByte
            case 0x13: case 0x2E: Obj(ref p, r); Expr(d, ref p, end, r); break;                       // MetaCast, DynamicCast
            case 0x07: p += 2; Expr(d, ref p, end, r); break;                                          // JumpIfNot
            case 0x2F: Expr(d, ref p, end, r); p += 2; break;                                          // Iterator
            case 0x05: Obj(ref p, r); p++; Expr(d, ref p, end, r); break;                              // Switch
            case 0x09: p += 3; Expr(d, ref p, end, r); break;                                          // Assert: line, debug mode
            case 0x0A: { int w = d[p] | d[p + 1] << 8; p += 2; if (w != 0xFFFF) Expr(d, ref p, end, r); break; }   // Case
            case 0x0C:                                                                                 // LabelTable: (name, code offset) to None
                while (true) { int at = p; Name(ref p, r); p += 4; if (I32(d, at) < 0) throw new InvalidDataException("label name"); if (p > end) throw new InvalidDataException("label table past the end"); if (LabelEnd(d, at)) break; }
                break;
            case 0x0D: Expr(d, ref p, end, r); break;                                                  // GotoLabel
            case 0x18: p += 2; Expr(d, ref p, end, r); break;                                          // Skip
            case 0x49: p += 2; OptionalDebug(d, ref p, end, r); Expr(d, ref p, end, r); OptionalDebug(d, ref p, end, r); p++; break;   // DefaultParmValue
            case 0x32: case 0x33: Obj(ref p, r); Expr(d, ref p, end, r); Expr(d, ref p, end, r); break;   // StructCmpEq / Ne
            case 0x35: Obj(ref p, r); Obj(ref p, r); p += 2; Expr(d, ref p, end, r); break;            // StructMember: property, struct, two bytes
            case 0x43: Name(ref p, r); Obj(ref p, r); break;                                            // DelegateProperty
            case 0x4B: Name(ref p, r); break;                                                           // InstanceDelegate
            case 0x3B: case 0x3C: case 0x3D: case 0x3E: Params(d, ref p, end, r); OptionalDebug(d, ref p, end, r); break;   // delegate compares
            case 0x5A: p += 2; break;                                                                   // JumpIfFilterEditorOnly
            default: throw new InvalidDataException($"token 0x{t:X2} not known");
        }
        return t;
    }

    /// <summary>A label table entry with no name ends the table; the reader only knows names by index, so the caller checks
    /// the name against the package's "None" (this is only used where the table ends, LabelEnd stays conservative).</summary>
    static bool LabelEnd(byte[] d, int at) => labelNone >= 0 && I32(d, at) == labelNone;

    [ThreadStatic] static int labelNone;

    /// <summary>The package's name index of "None", for label tables (set before walking a package's code; -1 = unknown).</summary>
    public static void UseNoneName(int index) => labelNone = index;
}
