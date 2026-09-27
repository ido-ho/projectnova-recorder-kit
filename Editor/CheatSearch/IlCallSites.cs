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
            public Site(MethodBase callee, string? literal) { Callee = callee; FirstLiteral = literal; }
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
        public static List<Site> Scan(MethodBase method, Func<MethodBase, bool> wanted)
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
                if (callee != null && wanted(callee)) sites.Add(new Site(callee, literal));
                literal = null;
            }
            return sites;
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
