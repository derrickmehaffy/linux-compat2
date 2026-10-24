using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using ClientPlugin.Tools;
using HarmonyLib;
using Keen.Game2.Client.GameSystems;
using Keen.VRage.Render12.EngineComponents;
using KeenThreadPool = Keen.VRage.Library.Threading.ThreadPool;

namespace ClientPlugin.Patches.SystemAbstraction;

/// <summary>
/// Shared rewrite for code that turns a raw timestamp into a <c>TimeSpan</c> with
/// <c>new TimeSpan(Stopwatch.GetTimestamp())</c>. That treats the timestamp as 100 ns ticks,
/// which only holds for the 10 MHz Windows timestamp; on Linux it counts nanoseconds, so every
/// difference of two such values comes out 100 times too long. Each matched
/// <c>Stopwatch.GetTimestamp</c> call is swapped for <see cref="TimestampTicks" />, which
/// returns the same instant in 100 ns ticks, and the <c>TimeSpan</c> constructor stays.
/// </summary>
internal static class TimestampTimeSpan
{
    private static readonly MethodInfo GetTimestamp = TranspilerHelpers.FindMethod(
        typeof(Stopwatch),
        nameof(Stopwatch.GetTimestamp)
    );

    private static readonly ConstructorInfo TimeSpanFromTicks = AccessTools.DeclaredConstructor(
        typeof(TimeSpan),
        [typeof(long)]
    );

    /// <summary>The current timestamp in 100 ns ticks. Time since boot cannot overflow.</summary>
    public static long TimestampTicks() => Stopwatch.GetElapsedTime(0).Ticks;

    public static int CountSites(MethodBase method) =>
        Sites(PatchProcessor.GetOriginalInstructions(method)).Count;

    public static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = [.. instructions];
        List<int> sites = Sites(il);
        int timestampCalls = il.Count(ci => TranspilerHelpers.CallsMethod(ci, GetTimestamp));
        TranspilerHelpers.AssertCount(
            sites.Count,
            timestampCalls,
            "Stopwatch.GetTimestamp calls wrapped in new TimeSpan(long)"
        );
        MethodInfo replacement = TranspilerHelpers.FindMethod(
            typeof(TimestampTimeSpan),
            nameof(TimestampTicks)
        );
        foreach (int i in sites)
        {
            CodeInstruction call = new(OpCodes.Call, replacement);
            call.labels.AddRange(il[i].labels);
            call.blocks.AddRange(il[i].blocks);
            il[i] = call;
        }
        return il;
    }

    private static List<int> Sites(List<CodeInstruction> il)
    {
        List<int> sites = [];
        for (int i = 0; i + 1 < il.Count; i++)
        {
            if (
                TranspilerHelpers.CallsMethod(il[i], GetTimestamp)
                && (il[i + 1].opcode == OpCodes.Newobj || il[i + 1].opcode == OpCodes.Call)
                && il[i + 1].operand is ConstructorInfo ctor
                && ctor == TimeSpanFromTicks
            )
                sites.Add(i);
        }
        return sites;
    }
}

/// <summary>
/// <c>Render12EngineComponent</c> and its nested types time the render thread with
/// <c>new TimeSpan(Stopwatch.GetTimestamp())</c>: present, frame and work durations, the
/// render-frame guard stopwatches, and the shift between the main thread's update and the
/// render thread's message processing. On Linux all of these come out 100 times too long.
/// The shift feeds <c>ClientToRenderMinimizer</c>, whose 2 ms / 4 ms thresholds then read as
/// 20 µs / 40 µs, so it adds a 1 ms delay to almost every main-loop frame. The frame and work
/// times feed the render statistics, the FPS counter and dynamic resolution, which then sees
/// the CPU as the limit. Rewriting every site gives them all real durations. This replaces the
/// former <c>FramePacer.OnUpdatedRenderWorkTime</c> conversion, whose input now arrives correct.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory("Finish")]
internal static class Render12EngineTimingPatch
{
    private const int ExpectedSites = 14;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        List<MethodBase> targets = [];
        int sites = 0;
        foreach (Type type in WithNested(typeof(Render12EngineComponent)))
        {
            if (type.ContainsGenericParameters)
                continue;
            foreach (
                MethodBase method in type.GetMethods(AccessTools.allDeclared)
                    .Cast<MethodBase>()
                    .Concat(type.GetConstructors(AccessTools.allDeclared))
            )
            {
                if (
                    method.IsAbstract
                    || method.ContainsGenericParameters
                    || method.GetMethodBody() == null
                )
                    continue;
                int count = TimestampTimeSpan.CountSites(method);
                if (count == 0)
                    continue;
                targets.Add(method);
                sites += count;
            }
        }
        TranspilerHelpers.AssertCount(
            sites,
            ExpectedSites,
            "timestamp TimeSpan sites in Render12EngineComponent"
        );
        return targets;
    }

    private static IEnumerable<Type> WithNested(Type type) =>
        type.GetNestedTypes(AccessTools.all).SelectMany(WithNested).Prepend(type);

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    ) => TimestampTimeSpan.Rewrite(instructions);
}

/// <summary>
/// <c>ThreadPool.Enqueue</c> orders queued work by <c>deadline.Time + new
/// TimeSpan(Stopwatch.GetTimestamp())</c>. On Linux the "now" term runs 100 times too fast
/// against the real deadline offsets, so a job's deadline is worth a hundredth of its value:
/// a fresh one-frame job lines up behind background streaming work queued about ten seconds
/// earlier instead of about a thousand. The key is only used to sort the queue.
/// </summary>
[HarmonyPatch(typeof(KeenThreadPool), nameof(KeenThreadPool.Enqueue))]
[HarmonyPatchCategory("Finish")]
internal static class ThreadPoolDeadlinePatch
{
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    ) => TimestampTimeSpan.Rewrite(instructions);

    private static void Prepare(MethodBase? original)
    {
        if (original != null)
            TranspilerHelpers.AssertCount(
                TimestampTimeSpan.CountSites(original),
                1,
                "timestamp TimeSpan sites in ThreadPool.Enqueue"
            );
    }
}

/// <summary>
/// Armor render regeneration gets 0.5 ms per frame: the deadline is
/// <c>Stopwatch.GetTimestamp()</c> plus <c>TimeSpan.FromMilliseconds(0.5).Ticks</c>. On Linux
/// those 5000 ticks are 5 µs, so armor sector updates after building, welding, grinding or
/// damage are postponed frame after frame and large grids catch up sector by sector.
/// </summary>
[HarmonyPatch(
    typeof(ArmorRenderRegenerationSessionComponent),
    nameof(ArmorRenderRegenerationSessionComponent.OnRenderArmorRegenerationPreparation)
)]
[HarmonyPatchCategory("Finish")]
internal static class ArmorRenderRegenerationBudgetPatch
{
    static void Postfix(ArmorRenderRegenerationSessionComponent __instance)
    {
        if (__instance._deterministicConfiguration.DeterministicRuntime)
            return;
        __instance.RenderArmorRegenerationDeadline =
            Stopwatch.GetTimestamp()
            + __instance._renderArmorRegenerationTimeLimit
                * Stopwatch.Frequency
                / TimeSpan.TicksPerSecond;
    }
}
