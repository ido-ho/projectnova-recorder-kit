using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Learn-and-drive v3 P3 (§3.3 source 2) — THE CALL SITES in one method's compiled body: every <c>call</c> /
    /// <c>callvirt</c>, with the FIRST string literal (<c>ldstr</c>) loaded since the previous call. For
    /// <c>Register("Skip 10", () => …)</c> the compiler emits <c>ldstr "Skip 10"</c>, the delegate, then the call — so
    /// the literal is the registration's name. A name built at run time (<c>$"Level {i}"</c>) is a call to
    /// <c>string.Format</c> first, which resets the literal: the site reads as "no literal", never a guessed name.
    ///
    /// METADATA ONLY: it decodes the IL bytes with the runtime's own opcode table (<see cref="OpCodes"/>) — a byte grep
    /// would read an operand as an opcode — and resolves tokens through the module. Nothing of the game runs.
    /// </summary>
    internal static class IlCallSites
    {
        internal readonly struct Site
        {
            public readonly MethodBase Callee;
            public readonly string? FirstLiteral;
            /// <summary>Fix 4 (dev setters): the integer constant loaded by the opcode RIGHT BEFORE the call
            /// (<c>ldc.i4.0</c> → 0 — how <c>SetActive(false)</c> and <c>enabled = false</c> compile), else null.</summary>
            public readonly int? PrecedingI4;
            /// <summary>The Fix 4 audit, M5: the call's receiver is the method's own object — <c>ldarg.0</c>, then the one
            /// constant argument (<c>this.enabled = false</c>) — read off the three opcodes before the call (nops skipped).</summary>
            public readonly bool ReceiverIsThis;
            /// <summary>…or its own GameObject: <c>ldarg.0</c>, <c>get_gameObject</c>, the constant (<c>gameObject.SetActive(false)</c>).</summary>
            public readonly bool ReceiverIsOwnGameObject;
            public Site(MethodBase callee, string? literal, int? precedingI4 = null, bool receiverIsThis = false, bool receiverIsOwnGameObject = false)
            {
                Callee = callee; FirstLiteral = literal; PrecedingI4 = precedingI4;
                ReceiverIsThis = receiverIsThis; ReceiverIsOwnGameObject = receiverIsOwnGameObject;
            }
        }

        private static readonly OpCode[] OneByte = new OpCode[0x100];
        private static readonly OpCode[] TwoByte = new OpCode[0x100];

        static IlCallSites()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is not OpCode op) continue;
                var v = (ushort)op.Value;
                if (v < 0x100) OneByte[v] = op;
                else if ((v & 0xff00) == 0xfe00) TwoByte[v & 0xff] = op;
            }
        }

        /// <summary>Every call site of <paramref name="method"/> whose callee <paramref name="wanted"/> accepts. Never
        /// throws: a body that cannot be read or decoded yields what was read before the problem.</summary>
        public static List<Site> Scan(MethodBase method, Func<MethodBase, bool> wanted) => Scan(method, wanted, null);

        /// <summary><see cref="Scan(MethodBase, Func{MethodBase, bool})"/>, also handing every STATIC FIELD the body loads
        /// (<c>ldsfld</c>) to <paramref name="staticFieldLoad"/> — a release flag kept in a field is read that way, not by a
        /// call (Fix 4). Resolving a field token runs nothing of the game.</summary>
        public static List<Site> Scan(MethodBase method, Func<MethodBase, bool> wanted, Action<FieldInfo>? staticFieldLoad)
        {
            var sites = new List<Site>();
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (Exception)
            {
                return sites;
            }
            if (il == null) return sites;
            var module = method.Module;
            Type[]? typeArgs = null, methodArgs = null;
            try
            {
                if (method.DeclaringType is { IsGenericType: true } dt) typeArgs = dt.GetGenericArguments();
                if (method.IsGenericMethod) methodArgs = method.GetGenericArguments();
            }
            catch (Exception) { }

            string? literal = null;
            int? lastI4 = null;
            char h1 = '.', h2 = '.', h3 = '.';
            var i = 0;
            while (i < il.Length)
            {
                OpCode op;
                if (il[i] == 0xfe)
                {
                    if (i + 1 >= il.Length) break;
                    op = TwoByte[il[i + 1]];
                    i += 2;
                }
                else
                {
                    op = OneByte[il[i]];
                    i += 1;
                }
                if (op.Size == 0) break; // a byte no opcode has: stop rather than misread the rest
                var operandAt = i;
                var size = OperandSize(op.OperandType, il, i);
                if (size < 0 || operandAt + size > il.Length) break;
                i += size;
                if (op.Value == OpCodes.Nop.Value) continue;
                var i4 = I4Of(op, il, operandAt);
                var before = lastI4;
                lastI4 = i4;
                // the three opcodes before this one: 'T' ldarg.0, 'C' a constant, 'G' get_gameObject, '.' anything else
                var (p1, p2, p3) = (h1, h2, h3);
                h3 = h2; h2 = h1;
                h1 = op.Value == OpCodes.Ldarg_0.Value ? 'T' : i4 != null ? 'C' : '.';
                if (staticFieldLoad != null && op.Value == OpCodes.Ldsfld.Value)
                {
                    FieldInfo? field = null;
                    try { field = module.ResolveField(BitConverter.ToInt32(il, operandAt), typeArgs, methodArgs); }
                    catch (Exception) { }
                    if (field != null) staticFieldLoad(field);
                    continue;
                }

                if (op.Value == OpCodes.Ldstr.Value)
                {
                    if (literal == null)
                    {
                        try { literal = module.ResolveString(BitConverter.ToInt32(il, operandAt)); }
                        catch (Exception) { }
                    }
                    continue;
                }
                if (op.Value != OpCodes.Call.Value && op.Value != OpCodes.Callvirt.Value) continue;
                MethodBase? callee = null;
                try { callee = module.ResolveMethod(BitConverter.ToInt32(il, operandAt), typeArgs, methodArgs); }
                catch (Exception) { }
                if (callee != null && callee.Name == "get_gameObject") h1 = 'G';
                if (callee != null && wanted(callee))
                    sites.Add(new Site(callee, literal, before,
                        receiverIsThis: p1 == 'C' && p2 == 'T',
                        receiverIsOwnGameObject: p1 == 'C' && p2 == 'G' && p3 == 'T'));
                literal = null;
            }
            return sites;
        }

        /// <summary>The value an <c>ldc.i4*</c> opcode loads, else null.</summary>
        private static int? I4Of(OpCode op, byte[] il, int operandAt)
        {
            var v = op.Value;
            if (v == OpCodes.Ldc_I4_M1.Value) return -1;
            if (v >= OpCodes.Ldc_I4_0.Value && v <= OpCodes.Ldc_I4_8.Value) return v - OpCodes.Ldc_I4_0.Value;
            if (v == OpCodes.Ldc_I4_S.Value) return (sbyte)il[operandAt];
            if (v == OpCodes.Ldc_I4.Value) return BitConverter.ToInt32(il, operandAt);
            return null;
        }

        private static int OperandSize(OperandType t, byte[] il, int at)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (at + 4 > il.Length) return -1;
                    return 4 + 4 * BitConverter.ToInt32(il, at);
                default: return 4;
            }
        }
    }
}
