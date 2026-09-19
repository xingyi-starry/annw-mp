using System;
using System.Collections.Generic;
using System.Reflection;
using ANNW;

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
        if (EnumValue("NextUnit") != 3 || EnumValue("StandByAndNext") != 4 ||
            EnumValue("ToggleSleep") != 6 || EnumValue("SelfDestroy") != 7 ||
            EnumValue("SetUnitToStay") != 12)
            missing.Add("ANNW.InputAction expected values");
        if (assembly.GetType("ANNW.FUI_WorldCursor")?.GetMethod("ConfirmAtCursor", BindingFlags.Instance | BindingFlags.NonPublic) is null ||
            assembly.GetType("ANNW.FUI_WorldCursor")?.GetMethod("CancelAtCursor", BindingFlags.Instance | BindingFlags.NonPublic) is null)
            missing.Add("FUI_WorldCursor keyboard input");
        if (typeof(UI_POP_PauseMenu).GetMethod("OnBtn_EnableEditor") is null || typeof(SUI_DBG_BATTLE).GetMethod("Show") is null)
            missing.Add("battle editor entry points");
        return missing;
    }

    private static int EnumValue(string name) => Enum.TryParse(typeof(InputAction), name, out var value) ? Convert.ToInt32(value) : -1;
}
