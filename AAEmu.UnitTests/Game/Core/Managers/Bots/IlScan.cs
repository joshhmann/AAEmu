using System.Reflection;
using System.Reflection.Emit;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Shared IL-reference decoder for the DECISION-PATH PURITY guards
/// (<c>LootBrainTests.Decide_ReferencesNoEngineType</c>,
/// <c>TravelBrainTests.Decide_ReferencesNoEngineType</c>): every method body of a
/// pure decision type is decoded and each member/type token resolved, so a guard
/// can fail on the exact referencing member instead of on an inspected list.
/// </summary>
internal static class IlScan
{
    /// <summary>Every type/member a method body references (nulls for tokens the module cannot resolve).</summary>
    public static IEnumerable<Type?> ReferencedTypes(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            yield break;
        }

        if (il == null)
            yield break;

        var declaringType = method.DeclaringType;
        var typeArguments = declaringType is { IsGenericType: true } ? declaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var module = method.Module;
        var position = 0;

        while (position < il.Length)
        {
            int opCodeValue;
            if (il[position] == 0xFE)
            {
                if (position + 1 >= il.Length)
                    yield break;
                opCodeValue = 0xFE00 | il[position + 1];
                position += 2;
            }
            else
            {
                opCodeValue = il[position];
                position += 1;
            }

            if (!OpCodeByValue.TryGetValue((short)opCodeValue, out var opCode))
                yield break;

            if (opCode.OperandType == OperandType.InlineNone)
                continue;

            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField
                or OperandType.InlineType or OperandType.InlineTok)
            {
                if (position + 4 > il.Length)
                    yield break;
                var token = BitConverter.ToInt32(il, position);
                yield return TryResolve(module, token, typeArguments, methodArguments);
                position += 4;
                continue;
            }

            position = SkipOperand(il, position, opCode.OperandType);
            if (position < 0)
                yield break;
        }
    }

    private static int SkipOperand(byte[] il, int position, OperandType operandType) => operandType switch
    {
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => position + 1,
        OperandType.InlineVar => position + 2,
        OperandType.InlineI8 or OperandType.InlineR => position + 8,
        OperandType.ShortInlineR => position + 4,
        OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig or OperandType.InlineString => position + 4,
        OperandType.InlineSwitch => position + 4 <= il.Length
            ? position + 4 + (4 * BitConverter.ToInt32(il, position))
            : -1,
        _ => position
    };

    private static Type? TryResolve(Module module, int token, Type[]? typeArguments, Type[]? methodArguments)
    {
        if (token == 0)
            return null;
        try
        {
            if (module.ResolveMember(token, typeArguments, methodArguments) is { } member)
            {
                return member switch
                {
                    Type resolved => resolved.IsGenericType ? resolved.GetGenericTypeDefinition() : resolved,
                    _ => member.DeclaringType is { IsGenericType: true } declaring
                        ? declaring.GetGenericTypeDefinition()
                        : member.DeclaringType
                };
            }

            var type = module.ResolveType(token, typeArguments, methodArguments);
            return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodeTable();

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
                continue;
            var opCode = (OpCode)field.GetValue(null)!;
            table[opCode.Value] = opCode;
        }

        return table;
    }
}
