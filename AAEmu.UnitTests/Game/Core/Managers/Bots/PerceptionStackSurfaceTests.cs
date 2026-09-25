using System.Reflection;
using System.Reflection.Emit;

using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Perception stack GUARD (architectural): the radar/snapshot/delta trio is a
/// planning + diagnostic surface ONLY. It must never be reachable from a bot
/// decision path — <c>BotDecisionSelector</c>, <c>BotProposalPrecondition</c>,
/// <c>QuestObjectiveTargetSelector</c>, the scenario/decision families or the
/// step executors. Those paths consume <c>ActorObservation</c> /
/// <c>BotObservedContext</c>, whose shape this stack deliberately does NOT
/// touch (the decision inputs stay frozen).
///
/// Proof shape: an ASSEMBLY-SURFACE scan of the shipping assembly's IL. For
/// every type declared in AAEmu.Game other than the perception files
/// themselves, every method/constructor body is decoded and each member/type
/// token is resolved; any reference to a perception type fails the test naming
/// the offender. That is strictly stronger than checking a named list of
/// consumers — a newly added scenario or executor is covered by construction,
/// and the failure message names the referencing member.
///
/// A positive control runs the same scan over the perception types themselves:
/// their cross-file references MUST be found, otherwise a decoder that silently
/// finds nothing would pass the negative test vacuously.
/// </summary>
[NotInParallel]
public class PerceptionStackSurfaceTests
{
    /// <summary>Perception types (by full name) that no shipping type may reference.</summary>
    private static readonly HashSet<string> PerceptionTypeNames = new(StringComparer.Ordinal)
    {
        typeof(BotRadarProjection).FullName!,
        typeof(RadarSnapshot).FullName!,
        typeof(RadarEntity).FullName!,
        typeof(RadarEntityKind).FullName!,
        typeof(BotPerceptionSnapshot).FullName!,
        typeof(BotPerceptionSnapshot.Snapshot).FullName!,
        typeof(PerceivedEntity).FullName!,
        typeof(PerceivedNpc).FullName!,
        typeof(PerceivedDoodad).FullName!,
        typeof(PerceivedCorpse).FullName!,
        typeof(PerceptionDelta).FullName!,
        typeof(PerceptionFrameDiff).FullName!,
        typeof(PerceptionLifecycle).FullName!,
        typeof(EntityMove).FullName!,
        typeof(EntityHealthChange).FullName!
    };

    private static readonly Assembly ShippingAssembly = typeof(BotRadarProjection).Assembly;

    [Test]
    public async Task PerceptionTypes_AreReferencedByNoShippingTypeOutsideTheStack()
    {
        var offenders = new List<string>();
        var scanned = 0;
        var perceptionRefsFound = 0;

        foreach (var type in LoadedTypes(ShippingAssembly))
        {
            var isPerceptionFile = IsPerceptionType(type);
            foreach (var method in ScannableMethods(type))
            {
                if (method.GetMethodBody() == null)
                    continue;
                scanned++;
                foreach (var referenced in ReferencedTypes(method))
                {
                    if (!IsPerceptionType(referenced))
                        continue;
                    perceptionRefsFound++;
                    if (!isPerceptionFile)
                        offenders.Add($"{type.FullName}.{method.Name} → {referenced.FullName}");
                }
            }
        }

        // Sanity: the scan really walked the assembly (and can see references).
        await Assert.That(scanned).IsGreaterThan(1000)
            .Because("the IL scan must actually walk the shipping assembly");
        await Assert.That(perceptionRefsFound).IsGreaterThan(0)
            .Because("the decoder must be able to SEE perception references (the stack references itself) — " +
                     "otherwise the negative assertion below would pass vacuously");

        await Assert.That(offenders).IsEmpty()
            .Because("the perception stack is a planning/diagnostic surface: no decision selector, precondition, " +
                     "scenario or executor may reference it — offenders: " + string.Join("; ", offenders.Take(20)));
    }

    [Test]
    public async Task DecisionInputs_DoNotCarryPerceptionTypes()
    {
        // The decision inputs are frozen: neither the observation snapshot nor
        // the decision context may grow a perception-typed member.
        foreach (var inputType in (Type[])[typeof(ActorObservation), typeof(BotObservedContext)])
        {
            foreach (var property in inputType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                await Assert.That(IsPerceptionType(property.PropertyType)).IsFalse()
                    .Because($"{inputType.Name}.{property.Name} must not carry a perception type");
            }

            foreach (var field in inputType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                await Assert.That(IsPerceptionType(field.FieldType)).IsFalse()
                    .Because($"{inputType.Name}.{field.Name} must not carry a perception type");
            }
        }
    }

    [Test]
    public async Task PerceptionStack_LivesInTheBotsNamespace()
    {
        foreach (var name in PerceptionTypeNames)
            await Assert.That(name).StartsWith("AAEmu.Game.Core.Managers.Bots.");
    }

    // ---------------------------------------------------------------- IL scan

    private static IEnumerable<Type> LoadedTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(t => t != null)!];
        }

        foreach (var type in types)
            yield return type;
    }

    private static IEnumerable<MethodBase> ScannableMethods(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(flags))
            yield return method;
        foreach (var ctor in type.GetConstructors(flags))
            yield return ctor;
    }

    private static bool IsPerceptionType(Type type)
    {
        for (var current = Normalize(type); current != null; current = current.DeclaringType == null ? null : Normalize(current.DeclaringType))
        {
            if (PerceptionTypeNames.Contains(current.FullName!))
                return true;
        }

        if (type.HasElementType && type.GetElementType() is { } element && IsPerceptionType(element))
            return true;

        if (!type.IsGenericType)
            return false;
        foreach (var argument in type.GetGenericArguments())
        {
            if (IsPerceptionType(argument))
                return true;
        }

        return false;
    }

    private static Type Normalize(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : type;

    /// <summary>
    /// Every type a method body references through a member/type token. A body
    /// that cannot be resolved on this runtime is skipped rather than failing
    /// the scan (the token belongs to a type the host did not load).
    /// </summary>
    private static IEnumerable<Type> ReferencedTypes(MethodBase method)
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
                    yield break; // truncated prefix
                opCodeValue = 0xFE00 | il[position + 1];
                position += 2;
            }
            else
            {
                opCodeValue = il[position];
                position += 1;
            }

            if (!OpCodeByValue.TryGetValue((short)opCodeValue, out var opCode))
                yield break; // unknown encoding — stop rather than misalign

            var operandType = opCode.OperandType;
            if (operandType == OperandType.InlineNone)
                continue;

            if (operandType is OperandType.InlineMethod or OperandType.InlineField or
                OperandType.InlineType or OperandType.InlineTok)
            {
                if (position + 4 > il.Length)
                    yield break;
                var token = BitConverter.ToInt32(il, position);
                if (TryResolve(module, token, typeArguments, methodArguments) is { } referenced)
                    yield return referenced;
                position += 4;
                continue;
            }

            position = SkipOperand(il, position, operandType);
            if (position < 0)
                yield break;
        }
    }

    /// <summary>Advances past a non-token operand; -1 when it runs off the end.</summary>
    private static int SkipOperand(byte[] il, int position, OperandType operandType) => operandType switch
    {
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => position + 1,
        OperandType.InlineVar => position + 2,
        OperandType.InlineI8 or OperandType.InlineR => position + 8,
        OperandType.ShortInlineR => position + 4,
        OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig or
            OperandType.InlineString => position + 4,
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
            // A member/type token: try every family (the token table decides,
            // and InlineTok can carry any of them).
            if (module.ResolveMember(token, typeArguments, methodArguments) is { } member)
            {
                return member switch
                {
                    Type resolvedType => Normalize(resolvedType),
                    _ => DeclaringTypeOf(member)
                };
            }

            return Normalize(module.ResolveType(token, typeArguments, methodArguments));
        }
        catch
        {
            // A token the host cannot resolve (unloaded/generic context) is not
            // evidence of a reference; the scan reports what it can read.
            return null;
        }
    }

    private static Type? DeclaringTypeOf(MemberInfo? member) =>
        member?.DeclaringType == null ? null : Normalize(member.DeclaringType);

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
