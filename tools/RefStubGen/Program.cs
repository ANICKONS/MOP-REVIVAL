// RefStubGen — compile-only reference stub generator.
//
// Reads the metadata of known-good compiled assemblies (e.g. the original MOP.dll and MSCLoader.dll,
// both compiled against the REAL game assemblies) and reconstructs the exact API surface they use
// from the game's assemblies (UnityEngine, PlayMaker, Assembly-CSharp, ES2, ...):
//   * TypeRefs  -> types (namespace, nesting, generic arity, value type vs class)
//   * MemberRefs -> methods / constructors / fields / properties / operators with exact signatures
//   * IL scan   -> static vs instance fields (ldsfld/stsfld)
//   * overrides in derived types -> virtual members on the base stub
// A hand-written overlay (refs/overlay/<Assembly>.cs) adds base types, enums and members that the
// mod uses but the input binaries do not. Generated types are partial so the overlay merges in.
//
// Usage:
//   RefStubGen --out <dir> --overlay <dir> --targets UnityEngine,PlayMaker,... <input.dll> [...]

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace RefStubGen
{
    // ---------------------------------------------------------------------------------------------
    // Model
    // ---------------------------------------------------------------------------------------------

    internal enum TypeKind { Unknown, Class, Struct, Enum, Interface, Delegate }

    internal sealed class StubType
    {
        public string Assembly;
        public string Namespace;      // only for top-level types
        public string Name;           // without generic arity suffix
        public int Arity;
        public StubType Declaring;
        public readonly List<StubType> Nested = new List<StubType>();
        public bool SeenValueType, SeenClass, IsInheritedLocally;
        public TypeKind Kind;
        public readonly Dictionary<string, StubMember> Members = new Dictionary<string, StubMember>();

        public string Key => (Declaring != null ? Declaring.Key + "/" : Assembly + "|" + Namespace + ".") + Name + "`" + Arity;

        public string CSharpReference(IList<TypeSig> args = null)
        {
            string prefix = Declaring != null
                ? Declaring.CSharpReference(null) + "."
                : "global::" + (string.IsNullOrEmpty(Namespace) ? "" : Namespace + ".");
            string generic = "";
            if (Arity > 0)
            {
                generic = args != null && args.Count == Arity
                    ? "<" + string.Join(", ", args.Select(a => a.Render())) + ">"
                    : "<" + string.Join(", ", Enumerable.Range(0, Arity).Select(i => "T" + i)) + ">";
            }
            return prefix + Program.Escape(Name) + generic;
        }
    }

    internal enum MemberKind { Method, Ctor, Field }

    internal sealed class StubMember
    {
        public MemberKind Kind;
        public string Name;
        public bool IsStatic;
        public bool IsVirtual;
        public int GenericArity;
        public TypeSig Return;
        public List<TypeSig> Params = new List<TypeSig>();

        public string SignatureKey =>
            Kind + ":" + Name + "`" + GenericArity + "(" + string.Join(",", Params.Select(p => p.Render())) + ")" +
            (Kind == MemberKind.Field ? ":" + Return.Render() : "");
    }

    // ---------------------------------------------------------------------------------------------
    // Type signatures
    // ---------------------------------------------------------------------------------------------

    internal abstract class TypeSig
    {
        public abstract string Render();
        public virtual bool IsByRef => false;
    }

    internal sealed class PrimitiveSig : TypeSig
    {
        public readonly string Keyword;
        public PrimitiveSig(string k) { Keyword = k; }
        public override string Render() => Keyword;
    }

    internal sealed class ExternalSig : TypeSig
    {
        public readonly string FullName; // global::NS.Name
        public readonly int Arity;
        public ExternalSig(string fullName, int arity) { FullName = fullName; Arity = arity; }
        public override string Render() => FullName + (Arity > 0 ? "<" + string.Join(", ", Enumerable.Repeat("object", Arity)) + ">" : "");
    }

    internal sealed class StubSig : TypeSig
    {
        public readonly StubType Type;
        public StubSig(StubType t) { Type = t; }
        public override string Render() => Type.CSharpReference();
    }

    internal sealed class GenericInstSig : TypeSig
    {
        public readonly TypeSig Definition;
        public readonly ImmutableArray<TypeSig> Args;
        public GenericInstSig(TypeSig def, ImmutableArray<TypeSig> args) { Definition = def; Args = args; }
        public override string Render()
        {
            if (Definition is StubSig s) return s.Type.CSharpReference(Args);
            if (Definition is ExternalSig e) return e.FullName + "<" + string.Join(", ", Args.Select(a => a.Render())) + ">";
            return Definition.Render();
        }
    }

    internal sealed class ArraySig : TypeSig
    {
        public readonly TypeSig Element; public readonly int Rank;
        public ArraySig(TypeSig e, int rank) { Element = e; Rank = rank; }
        public override string Render() => Element.Render() + "[" + new string(',', Rank - 1) + "]";
    }

    internal sealed class ByRefSig : TypeSig
    {
        public readonly TypeSig Element;
        public ByRefSig(TypeSig e) { Element = e; }
        public override bool IsByRef => true;
        public override string Render() => Element.Render();
    }

    internal sealed class PointerSig : TypeSig
    {
        public readonly TypeSig Element;
        public PointerSig(TypeSig e) { Element = e; }
        public override string Render() => Element.Render() + "*";
    }

    internal sealed class GenericParamSig : TypeSig
    {
        public readonly int Index; public readonly bool IsMethod;
        public GenericParamSig(int i, bool m) { Index = i; IsMethod = m; }
        public override string Render() => (IsMethod ? "M" : "T") + Index;
    }

    // ---------------------------------------------------------------------------------------------
    // Signature provider
    // ---------------------------------------------------------------------------------------------

    internal sealed class SigProvider : ISignatureTypeProvider<TypeSig, object>
    {
        private readonly Program owner;
        public SigProvider(Program owner) { this.owner = owner; }

        public TypeSig GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            switch (typeCode)
            {
                case PrimitiveTypeCode.Boolean: return new PrimitiveSig("bool");
                case PrimitiveTypeCode.Byte: return new PrimitiveSig("byte");
                case PrimitiveTypeCode.SByte: return new PrimitiveSig("sbyte");
                case PrimitiveTypeCode.Char: return new PrimitiveSig("char");
                case PrimitiveTypeCode.Int16: return new PrimitiveSig("short");
                case PrimitiveTypeCode.UInt16: return new PrimitiveSig("ushort");
                case PrimitiveTypeCode.Int32: return new PrimitiveSig("int");
                case PrimitiveTypeCode.UInt32: return new PrimitiveSig("uint");
                case PrimitiveTypeCode.Int64: return new PrimitiveSig("long");
                case PrimitiveTypeCode.UInt64: return new PrimitiveSig("ulong");
                case PrimitiveTypeCode.Single: return new PrimitiveSig("float");
                case PrimitiveTypeCode.Double: return new PrimitiveSig("double");
                case PrimitiveTypeCode.IntPtr: return new PrimitiveSig("global::System.IntPtr");
                case PrimitiveTypeCode.UIntPtr: return new PrimitiveSig("global::System.UIntPtr");
                case PrimitiveTypeCode.Object: return new PrimitiveSig("object");
                case PrimitiveTypeCode.String: return new PrimitiveSig("string");
                case PrimitiveTypeCode.Void: return new PrimitiveSig("void");
                case PrimitiveTypeCode.TypedReference: return new PrimitiveSig("global::System.TypedReference");
                default: return new PrimitiveSig("object");
            }
        }

        public TypeSig GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => new PrimitiveSig("object"); // local (input-assembly) types never appear in target signatures

        public TypeSig GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => owner.ResolveTypeRef(reader, handle, rawTypeKind);

        public TypeSig GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public TypeSig GetSZArrayType(TypeSig elementType) => new ArraySig(elementType, 1);
        public TypeSig GetArrayType(TypeSig elementType, ArrayShape shape) => new ArraySig(elementType, shape.Rank);
        public TypeSig GetByReferenceType(TypeSig elementType) => new ByRefSig(elementType);
        public TypeSig GetPointerType(TypeSig elementType) => new PointerSig(elementType);
        public TypeSig GetPinnedType(TypeSig elementType) => elementType;
        public TypeSig GetModifiedType(TypeSig modifier, TypeSig unmodifiedType, bool isRequired) => unmodifiedType;
        public TypeSig GetGenericInstantiation(TypeSig genericType, ImmutableArray<TypeSig> typeArguments) => new GenericInstSig(genericType, typeArguments);
        public TypeSig GetGenericTypeParameter(object genericContext, int index) => new GenericParamSig(index, false);
        public TypeSig GetGenericMethodParameter(object genericContext, int index) => new GenericParamSig(index, true);
        public TypeSig GetFunctionPointerType(MethodSignature<TypeSig> signature) => new PrimitiveSig("global::System.IntPtr");
    }

    // ---------------------------------------------------------------------------------------------
    // Program
    // ---------------------------------------------------------------------------------------------

    internal sealed partial class Program
    {
        private readonly HashSet<string> targets;
        private readonly Dictionary<string, StubType> types = new Dictionary<string, StubType>();
        private readonly Dictionary<string, Version> versions = new Dictionary<string, Version>();
        private readonly Dictionary<string, HashSet<string>> dependencies = new Dictionary<string, HashSet<string>>();
        private readonly SigProvider provider;

        private Program(IEnumerable<string> targets)
        {
            this.targets = new HashSet<string>(targets, StringComparer.Ordinal);
            provider = new SigProvider(this);
        }

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
            "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
            "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long",
            "namespace","new","null","object","operator","out","override","params","private","protected","public",
            "readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct",
            "switch","this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using",
            "virtual","void","volatile","while"
        };

        internal static string Escape(string name) => CSharpKeywords.Contains(name) ? "@" + name : name;

        private static int Main(string[] args)
        {
            string outDir = null, overlayDir = null;
            List<string> targetList = new List<string>();
            List<string> inputs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out": outDir = args[++i]; break;
                    case "--overlay": overlayDir = args[++i]; break;
                    case "--targets": targetList.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                    default: inputs.Add(args[i]); break;
                }
            }

            if (outDir == null || targetList.Count == 0 || inputs.Count == 0)
            {
                Console.Error.WriteLine("Usage: RefStubGen --out <dir> [--overlay <dir>] --targets A,B,C <input.dll>...");
                return 2;
            }

            Program p = new Program(targetList);
            foreach (string input in inputs)
                p.Harvest(input);

            p.Emit(outDir, overlayDir);
            return 0;
        }

        // -----------------------------------------------------------------------------------------
        // Harvesting
        // -----------------------------------------------------------------------------------------

        private string currentInputAssemblyTarget; // dependency tracking: which target assembly references which

        private void Harvest(string path)
        {
            Console.WriteLine("[RefStubGen] Reading " + Path.GetFileName(path));
            using FileStream fs = File.OpenRead(path);
            using PEReader pe = new PEReader(fs);
            MetadataReader md = pe.GetMetadataReader();

            string selfName = md.GetString(md.GetAssemblyDefinition().Name);
            currentInputAssemblyTarget = targets.Contains(selfName) ? selfName : null;

            foreach (AssemblyReferenceHandle h in md.AssemblyReferences)
            {
                AssemblyReference ar = md.GetAssemblyReference(h);
                string name = md.GetString(ar.Name);
                if (!targets.Contains(name)) continue;
                if (!versions.TryGetValue(name, out Version v) || ar.Version > v)
                    versions[name] = ar.Version;
            }

            // 1) Static field detection via IL scan.
            HashSet<int> staticFieldTokens = ScanStaticFieldRefs(pe, md);

            // 2) Member references.
            foreach (MemberReferenceHandle h in md.MemberReferences)
            {
                MemberReference mr = md.GetMemberReference(h);
                StubType owner = ResolveParent(md, mr.Parent);
                if (owner == null) continue;

                string name = md.GetString(mr.Name);
                try
                {
                    if (mr.GetKind() == MemberReferenceKind.Field)
                    {
                        TypeSig ft = mr.DecodeFieldSignature(provider, null);
                        AddMember(owner, new StubMember
                        {
                            Kind = MemberKind.Field,
                            Name = name,
                            Return = ft,
                            IsStatic = staticFieldTokens.Contains(MetadataTokens.GetToken(h))
                        });
                    }
                    else
                    {
                        MethodSignature<TypeSig> sig = mr.DecodeMethodSignature(provider, null);
                        AddMember(owner, new StubMember
                        {
                            Kind = name == ".ctor" ? MemberKind.Ctor : MemberKind.Method,
                            Name = name,
                            IsStatic = !sig.Header.IsInstance,
                            GenericArity = sig.GenericParameterCount,
                            Return = sig.ReturnType,
                            Params = sig.ParameterTypes.ToList()
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RefStubGen] warn: cannot decode {owner.Key}::{name}: {ex.Message}");
                }
            }

            // 3) Local types deriving from target types: base must be an inheritable class with virtuals.
            foreach (TypeDefinitionHandle th in md.TypeDefinitions)
            {
                TypeDefinition td = md.GetTypeDefinition(th);
                if (td.BaseType.IsNil || td.BaseType.Kind != HandleKind.TypeReference) continue;

                TypeSig baseSig = ResolveTypeRef(md, (TypeReferenceHandle)td.BaseType, 0);
                if (baseSig is not StubSig baseStub) continue;

                StubType bt = baseStub.Type;
                bt.SeenClass = true;
                bt.IsInheritedLocally = true;

                foreach (MethodDefinitionHandle mh in td.GetMethods())
                {
                    MethodDefinition m = md.GetMethodDefinition(mh);
                    MethodAttributes a = m.Attributes;
                    bool isOverride = (a & MethodAttributes.Virtual) != 0 && (a & MethodAttributes.NewSlot) == 0;
                    if (!isOverride) continue;

                    string mname = md.GetString(m.Name);
                    if (mname is "Finalize" or "ToString" or "Equals" or "GetHashCode") continue;

                    try
                    {
                        MethodSignature<TypeSig> sig = m.DecodeSignature(provider, null);
                        AddMember(bt, new StubMember
                        {
                            Kind = MemberKind.Method,
                            Name = mname,
                            IsStatic = false,
                            IsVirtual = true,
                            GenericArity = sig.GenericParameterCount,
                            Return = sig.ReturnType,
                            Params = sig.ParameterTypes.ToList()
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[RefStubGen] warn: cannot decode override {mname}: {ex.Message}");
                    }
                }
            }
        }

        private void AddMember(StubType owner, StubMember m)
        {
            string key = m.SignatureKey + (m.IsStatic ? ":s" : ":i");
            if (owner.Members.TryGetValue(key, out StubMember existing))
            {
                existing.IsVirtual |= m.IsVirtual;
                return;
            }
            owner.Members[key] = m;
            TrackDependencies(owner.Assembly, m.Return);
            foreach (TypeSig p in m.Params) TrackDependencies(owner.Assembly, p);
        }

        private void TrackDependencies(string fromAssembly, TypeSig sig)
        {
            switch (sig)
            {
                case StubSig s:
                    if (s.Type.Assembly != fromAssembly)
                    {
                        if (!dependencies.TryGetValue(fromAssembly, out HashSet<string> set))
                            dependencies[fromAssembly] = set = new HashSet<string>();
                        set.Add(s.Type.Assembly);
                    }
                    break;
                case GenericInstSig g:
                    TrackDependencies(fromAssembly, g.Definition);
                    foreach (TypeSig a in g.Args) TrackDependencies(fromAssembly, a);
                    break;
                case ArraySig a: TrackDependencies(fromAssembly, a.Element); break;
                case ByRefSig b: TrackDependencies(fromAssembly, b.Element); break;
                case PointerSig ptr: TrackDependencies(fromAssembly, ptr.Element); break;
            }
        }

        private StubType ResolveParent(MetadataReader md, EntityHandle parent)
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeReference:
                    return (ResolveTypeRef(md, (TypeReferenceHandle)parent, 0) as StubSig)?.Type;
                case HandleKind.TypeSpecification:
                    TypeSig spec = md.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(provider, null);
                    if (spec is GenericInstSig g && g.Definition is StubSig gs) return gs.Type;
                    return (spec as StubSig)?.Type;
                default:
                    return null;
            }
        }

        internal TypeSig ResolveTypeRef(MetadataReader md, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference tr = md.GetTypeReference(handle);
            string rawName = md.GetString(tr.Name);
            string ns = md.GetString(tr.Namespace);
            int arity = 0;
            string name = rawName;
            int tick = rawName.IndexOf('`');
            if (tick >= 0)
            {
                arity = int.Parse(rawName.Substring(tick + 1));
                name = rawName.Substring(0, tick);
            }

            EntityHandle scope = tr.ResolutionScope;
            StubType declaring = null;
            string assembly;
            string externalPrefix;

            if (scope.Kind == HandleKind.TypeReference)
            {
                TypeSig outer = ResolveTypeRef(md, (TypeReferenceHandle)scope, 0);
                if (outer is StubSig os)
                {
                    declaring = os.Type;
                    assembly = declaring.Assembly;
                    externalPrefix = null;
                }
                else
                {
                    string outerName = outer.Render();
                    return new ExternalSig(outerName + "." + Escape(name), arity);
                }
            }
            else if (scope.Kind == HandleKind.AssemblyReference)
            {
                assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                externalPrefix = "global::" + (string.IsNullOrEmpty(ns) ? "" : ns + ".");
            }
            else
            {
                // Module-local reference: belongs to the input assembly itself.
                assembly = currentInputAssemblyTarget;
                externalPrefix = "global::" + (string.IsNullOrEmpty(ns) ? "" : ns + ".");
            }

            if (assembly == null || !targets.Contains(assembly))
                return new ExternalSig(externalPrefix + Escape(name), arity);

            string key = (declaring != null ? declaring.Key + "/" : assembly + "|" + ns + ".") + name + "`" + arity;
            if (!types.TryGetValue(key, out StubType t))
            {
                t = new StubType { Assembly = assembly, Namespace = declaring == null ? ns : null, Name = name, Arity = arity, Declaring = declaring };
                types[key] = t;
                declaring?.Nested.Add(t);
            }

            // rawTypeKind: 0x11 = ValueType, 0x12 = Class (SignatureTypeKind).
            if (rawTypeKind == (byte)SignatureTypeKind.ValueType) t.SeenValueType = true;
            else if (rawTypeKind == (byte)SignatureTypeKind.Class) t.SeenClass = true;

            return new StubSig(t);
        }

        // -----------------------------------------------------------------------------------------
        // IL scan for ldsfld / ldsflda / stsfld
        // -----------------------------------------------------------------------------------------

        private static readonly Dictionary<short, OperandType> OpTable = BuildOpTable();

        private static Dictionary<short, OperandType> BuildOpTable()
        {
            Dictionary<short, OperandType> table = new Dictionary<short, OperandType>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is OpCode op)
                    table[op.Value] = op.OperandType;
            }
            return table;
        }

        private static HashSet<int> ScanStaticFieldRefs(PEReader pe, MetadataReader md)
        {
            HashSet<int> result = new HashSet<int>();
            foreach (MethodDefinitionHandle mh in md.MethodDefinitions)
            {
                MethodDefinition m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;

                MethodBodyBlock body;
                try { body = pe.GetMethodBody(m.RelativeVirtualAddress); }
                catch { continue; }

                BlobReader il = body.GetILReader();
                while (il.RemainingBytes > 0)
                {
                    short value = il.ReadByte();
                    if (value == 0xFE) value = unchecked((short)(0xFE00 | il.ReadByte()));
                    if (!OpTable.TryGetValue(value, out OperandType operand)) break;

                    switch (operand)
                    {
                        case OperandType.InlineNone: break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar: il.Offset += 1; break;
                        case OperandType.InlineVar: il.Offset += 2; break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR: il.Offset += 8; break;
                        case OperandType.InlineSwitch:
                            int n = il.ReadInt32();
                            il.Offset += 4 * n;
                            break;
                        case OperandType.InlineField:
                            int token = il.ReadInt32();
                            // 0x7E ldsfld, 0x7F ldsflda, 0x80 stsfld
                            if ((value == 0x7E || value == 0x7F || value == 0x80) && (token >> 24) == 0x0A)
                                result.Add(token);
                            break;
                        default: il.Offset += 4; break;
                    }
                }
            }
            return result;
        }
    }
}
