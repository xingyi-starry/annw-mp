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
        "Player.DoWipeOut", "Player.proc_DoWipeOut", "UnitData.AddExp_BasePercent", "UnitData.Hurt", "UnitData.Die",
        "GameAPI.CreateUnit", "GameAPI.DescendUnit", "UnitData.TriggerDieEvent", "ActionData.DoActionCell",
        "UnitAI.ExecuteAction", "UndoMoveData.UndoLastMove", "GS_Battle.Save_General",
        "UnitData.SaveOb", "UnitData.SetMoveGoal", "UnitData.ClearMoveGoal", "UnitData.GetMoveZone",
        "ActionData.GetSelectZone", "AutoGuideController.TryAutoCommandSelectedUnits", "AutoGuideController.TryAutoCommandUnactedUnits"
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
        if (typeof(StartGameSetting).GetField("diff") is null ||
            typeof(UI_MENU_LevelSelect_InfoSkm).GetField("btn_diffSel") is null ||
            typeof(UI_MENU_LevelSelect_InfoSkm).GetField("cur_selected_diff", BindingFlags.Instance | BindingFlags.NonPublic) is null ||
            typeof(UI_MENU_LevelSelect_InfoSkm).GetMethod("RefreshDiffSelBtn", BindingFlags.Instance | BindingFlags.NonPublic) is null)
            missing.Add("v1.0.9 skirmish difficulty selection");
        if (typeof(SGS_Player).GetField("keep_map_co") is null ||
            typeof(UI_SKM_PlayerSetting).GetField("cur_selected_co_is_map_preset", BindingFlags.Instance | BindingFlags.NonPublic) is null ||
            typeof(UI_SKM_PlayerSetting).GetField("cur_map_co_name", BindingFlags.Instance | BindingFlags.NonPublic) is null ||
            typeof(UI_MENU_LevelSelect_InfoSkm).GetMethod("Render", new[] { typeof(string), typeof(string), typeof(ulong) }) is null ||
            typeof(Player).GetMethod("proc_DescendResidualUnits") is null)
            missing.Add("v1.0.11 map commander, workshop selection and defeated-player cleanup");
        if (typeof(GS_Battle).GetField("ai_playback_mode")?.FieldType != typeof(GS_Battle.AIPlaybackMode) ||
            typeof(GS_Battle).GetField("ai_skip_unrelated_actions")?.FieldType != typeof(bool) ||
            typeof(GS_Battle).GetField("ai_skip_non_battle_actions")?.FieldType != typeof(bool) ||
            typeof(GS_Battle).GetProperty("ai_playback_paused")?.PropertyType != typeof(bool) ||
            typeof(GS_Battle).GetProperty("ai_playback_wait_scale")?.PropertyType != typeof(float) ||
            typeof(GS_Battle).GetMethod("AIPause", Type.EmptyTypes) is null ||
            typeof(GS_Battle).GetMethod("AIWait", new[] { typeof(float) }) is null ||
            typeof(GS_Battle).GetMethod("ShouldSkipActionAnimation", new[] { typeof(UT_UnitAction) }) is null)
            missing.Add("v1.0.12 AI playback waits and animation filters");
        foreach (var name in new[] { "OnClick_Skip_Current", "OnClick_Skip", "OnClick_Cancel", "OnClick_Pause", "OnClick_Play",
                     "OnClick_Fast", "OnClick_ToggleSkipUnrelated", "OnClick_ToggleSkipNonBattle", "ShowAsAITurn", "ShowAsAutoGuide" })
            if (typeof(UI_POP_AITurn).GetMethod(name, Type.EmptyTypes) is null) missing.Add("UI_POP_AITurn." + name);
        foreach (var name in new[] { "tab_pause", "tab_play", "tab_fast", "tab_skip_unrelated", "tab_skip_nonbattle" })
            if (typeof(UI_POP_AITurn).GetField(name)?.FieldType != typeof(UI_TAB_BTN)) missing.Add("UI_POP_AITurn." + name);
        if (typeof(UI_POP_AITurn).GetField("btn_cancel")?.FieldType != typeof(UnityEngine.UI.Button))
            missing.Add("UI_POP_AITurn.btn_cancel");
        foreach (var name in new[] { "proc_AutoCommand", "StartAutoGuideBatch", "proc_BatchActionSingle" })
            if (typeof(AutoGuideController).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) is null)
                missing.Add("AutoGuideController." + name);
        if (typeof(AutoGuideController).GetField("active_batch", BindingFlags.Instance | BindingFlags.NonPublic) is null ||
            (int)UnitActionType.MOVE != (int)Protocol.ResolvedActionType.Move ||
            (int)UnitActionType.ACTION_NORMAL != (int)Protocol.ResolvedActionType.Action ||
            (int)UnitActionType.ACTION_CREATE != (int)Protocol.ResolvedActionType.Create ||
            (int)UnitActionType.ACTION_UNLOAD != (int)Protocol.ResolvedActionType.Unload)
            missing.Add("resolved native unit batch layout");
        foreach (var name in new[] { "DoAction_Normal", "DoAction_MultiTarget", "DoAction_Parallel", "DoAction_Field", "DoAction_Skill", "proc_DoActionCell", "DoActionAni" })
            if (typeof(ActionData).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.ReturnType != typeof(System.Collections.IEnumerator))
                missing.Add("ActionData." + name);
        if (typeof(GameAPI).GetMethod("proc_UnitDeathAnimation", BindingFlags.Instance | BindingFlags.NonPublic)?.ReturnType != typeof(System.Collections.IEnumerator))
            missing.Add("GameAPI.proc_UnitDeathAnimation");
        return missing;
    }

    private static int EnumValue(string name) => Enum.TryParse(typeof(InputAction), name, out var value) ? Convert.ToInt32(value) : -1;
}
