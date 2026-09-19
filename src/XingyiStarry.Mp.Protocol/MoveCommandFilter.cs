using System;
using System.Collections.Generic;

namespace XingyiStarry.Mp.Protocol;

public enum MoveUnitDecision { Include, Skip, Reject }

public static class MoveCommandFilter
{
    // Reject preserves the original command so the authority validator can report
    // ownership or identity errors instead of silently dropping a hostile unit.
    public static bool TryFilter(GameCommand command, Func<long, MoveUnitDecision> classify)
    {
        if (command.Kind != CommandKind.Move || command.UnitIds.Length != command.UnitTargetXs.Length ||
            command.UnitIds.Length != command.UnitTargetYs.Length) return false;
        var ids = new List<long>(); var xs = new List<int>(); var ys = new List<int>();
        for (var i = 0; i < command.UnitIds.Length; i++)
        {
            var decision = classify(command.UnitIds[i]);
            if (decision == MoveUnitDecision.Reject) return false;
            if (decision == MoveUnitDecision.Skip) continue;
            ids.Add(command.UnitIds[i]); xs.Add(command.UnitTargetXs[i]); ys.Add(command.UnitTargetYs[i]);
        }
        command.UnitIds = ids.ToArray(); command.UnitTargetXs = xs.ToArray(); command.UnitTargetYs = ys.ToArray();
        return true;
    }
}
