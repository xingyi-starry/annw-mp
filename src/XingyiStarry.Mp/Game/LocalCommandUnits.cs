using System.Collections.Generic;

namespace XingyiStarry.Mp.Game;

internal static class LocalCommandUnits
{
    internal static bool Owned(UnitData? unit) => unit is not null && GS_Battle.self is not null &&
        unit.player == GS_Battle.self.cur_player && !unit.dead && !unit.dying;

    internal static bool CanMove(UnitData? unit) => Owned(unit) && !unit!.moved &&
        !unit.in_animation && !unit.building;

    internal static List<UnitData> Filter(IEnumerable<UnitData> units, bool move = false)
    {
        var result = new List<UnitData>();
        foreach (var unit in units)
            if (move ? CanMove(unit) : Owned(unit) && !unit.in_animation) result.Add(unit);
        return result;
    }
}
