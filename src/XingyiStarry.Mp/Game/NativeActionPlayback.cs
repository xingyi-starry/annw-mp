using System.Collections;
using System.Collections.Generic;
using XingyiStarry.Mp.Protocol.Wire;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

// Native coroutines retain targeting visuals, visibility, sounds and timing.
// Their local settlement call sites are skipped; matching host steps perform
// the actual native writes. No animation is rebuilt by the dispatcher.
internal static class NativeActionPlayback
{
    private sealed class Phase
    {
        internal bool NativeFinished, HostFinished, Canceled;
        internal readonly bool Live;
        internal Phase(ActionData action) => Live = action is Action_Attack || action.sd_action.offensive;
    }
    private static readonly Dictionary<ActionData, Phase> phases = new Dictionary<ActionData, Phase>();
    private static Phase? active;
    private static int presenting;
    private static int moving, generation;
    internal static bool IsPresenting => presenting != 0;
    internal static IEnumerator Movement(IEnumerator native)
    {
        var currentGeneration = generation;
        moving++;
        return Move(native, currentGeneration);
    }
    private static IEnumerator Move(IEnumerator native, int currentGeneration)
    {
        try { yield return native; }
        finally { if (currentGeneration == generation) moving--; }
    }

    internal static IEnumerator Play(ActionData action, IEnumerator native)
    {
        var phase = new Phase(action); phases.Add(action, phase);
        return Run(action, phase, native);
    }
    private static IEnumerator Run(ActionData action, Phase phase, IEnumerator native)
    {
        try
        {
            yield return Presentation(native);
            if (phase.Canceled) yield break;
            phase.NativeFinished = true;
            if (!action.is_sub_action)
                while (!phase.HostFinished && !phase.Canceled) yield return 0f;
        }
        finally
        {
            if (phase.HostFinished && phases.TryGetValue(action, out var current) && current == phase) phases.Remove(action);
        }
    }
    internal static IEnumerator Presentation(IEnumerator native)
    {
        using var stack = new CoroutineStack(native);
        while (true)
        {
            bool more;
            try { presenting++; more = stack.MoveNext(); }
            finally { presenting--; }
            if (!more) yield break;
            yield return stack.Current;
        }
    }
    internal static bool MayApply(SettlementEvent value)
    {
        if (moving != 0) return false;
        if (value.AttackStarted is { } start)
            return phases.ContainsKey(AttackSettlements.ResolveAction(value, start.ActionName));
        var endingName = value.ActionCallbacks?.ActionName ?? (value.AttackFinished is { CheckAfterAction: false } finish ? finish.ActionName : null);
        if (endingName is not null && phases.TryGetValue(AttackSettlements.ResolveAction(value, endingName), out var ending))
            return ending.NativeFinished;
        if (active is null || active.Canceled) return true;
        return active.Live || active.NativeFinished;
    }
    internal static void Started(ActionData action)
    {
        if (!phases.TryGetValue(action, out var phase)) throw new System.IO.InvalidDataException("Action started before its native coroutine.");
        active = phase;
    }
    internal static void Finished(ActionData action)
    {
        if (!phases.TryGetValue(action, out var phase)) return;
        phase.HostFinished = true;
        if (phase.NativeFinished) phases.Remove(action);
        if (active == phase) active = null;
    }
    internal static void CompleteOperation() { foreach (var phase in phases.Values) phase.HostFinished = true; active = null; }
    internal static void Reset()
    {
        foreach (var phase in phases.Values) phase.Canceled = true;
        phases.Clear(); active = null; generation++; moving = 0;
    }
}
