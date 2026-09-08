using System;
using System.Collections.Generic;
using System.Reflection;

namespace XingyiStarry.Mp.Game;

internal static class GameCompatibility
{
    private static readonly string[] RequiredMethods =
    {
        "GameController.ExecuteAction", "GameController.NextTurn", "GameController.StartPlayerTurn", "GameController.EndPlayerTurn",
        "UX_Manager.proc_UnitsDoMove", "UX_Manager.proc_UnitsDoAction", "UX_Manager.proc_SkillDoAction",
        "UnitAI.ExecuteAction", "UndoMoveData.UndoLastMove", "GS_Battle.Save_General"
    };

    public static IReadOnlyList<string> Probe()
    {
        var missing = new List<string>();
        var assembly = typeof(GameController).Assembly;
        foreach (var entry in RequiredMethods)
        {
            var separator = entry.LastIndexOf('.');
            var typeName = entry.Substring(0, separator); var methodName = entry.Substring(separator + 1);
            var type = assembly.GetType(typeName) ?? assembly.GetType("ANNW." + typeName);
            if (type is null || type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is null) missing.Add(entry);
        }
        return missing;
    }
}
