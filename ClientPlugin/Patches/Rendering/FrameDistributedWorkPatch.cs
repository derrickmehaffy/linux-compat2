using System.Diagnostics;
using HarmonyLib;
using Keen.VRage.Render.FrameData;
using Keen.VRage.Render12.Core.Systems;

namespace ClientPlugin.Patches.Rendering;

/// <summary>
/// Gives the frame-distributed render work its real per-frame time budget.
/// <para>
/// <c>FrameDistributedWorkManager</c> spreads GPU model uploads, render command batch
/// replay, resource streaming and entity destruction over several frames, with a budget of
/// 2 ms per work type (more under pressure, 64 ms while loading). The time spent is measured
/// as a <c>Stopwatch.GetTimestamp</c> difference and committed with
/// <c>TimeSpan.FromTicks</c>, which assumes the 10 MHz Windows timestamp. On Linux the
/// timestamp counts nanoseconds, so every chunk of work is booked as 100 times longer than
/// it took and each work type gets about 20 µs per frame. Render command replay is cut the
/// same way: <c>ReplayBudget.Reset</c> adds the budget's <c>TimeSpan</c> ticks to a raw
/// timestamp. The renderer then falls behind the simulation, most visibly as voxel terrain
/// that keeps showing its old shape for a while after mining or around station pads.
/// </para>
/// </summary>
[HarmonyPatch(
    typeof(FrameDistributedWorkManager),
    nameof(FrameDistributedWorkManager.CommitElapsedTime)
)]
[HarmonyPatchCategory("Finish")]
internal static class FrameDistributedWorkElapsedPatch
{
    static void Prefix(ref TimeSpan elapsed)
    {
        elapsed = Stopwatch.GetElapsedTime(0, elapsed.Ticks);
    }
}

[HarmonyPatch(typeof(ReplayBudget), nameof(ReplayBudget.Reset))]
[HarmonyPatchCategory("Finish")]
internal static class ReplayBudgetResetPatch
{
    static void Postfix(ref long ____endTime, TimeSpan ____budget)
    {
        if (____endTime != long.MaxValue)
            ____endTime =
                Stopwatch.GetTimestamp() + (long)(____budget.TotalSeconds * Stopwatch.Frequency);
    }
}
