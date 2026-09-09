using ANNW;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(UI_POP_PauseMenu), nameof(UI_POP_PauseMenu.Show))]
internal static class MultiplayerPauseMenuPatch
{
    private static void Postfix(UI_POP_PauseMenu __instance)
    {
        if (!InputGate.MultiplayerActive) return;
        if (__instance.btn_save is not null) __instance.btn_save.interactable = false;
        if (__instance.btn_restart is not null) __instance.btn_restart.interactable = false;
        if (__instance.btn_load is null) return;
        __instance.btn_load.gameObject.SetActive(true);
        __instance.btn_load.interactable = XingyiStarryMpPlugin.Instance?.CanManualResync == true;
        foreach (var label in __instance.btn_load.GetComponentsInChildren<TMP_Text>(true))
            label.text = "重新同步";
    }
}

[HarmonyPatch(typeof(UI_POP_PauseMenu), nameof(UI_POP_PauseMenu.OnBtn_Load))]
internal static class MultiplayerPauseMenuResyncPatch
{
    private static bool Prefix(UI_POP_PauseMenu __instance)
    {
        if (!InputGate.MultiplayerActive) return true;
        XingyiStarryMpPlugin.Instance?.RequestManualResync();
        __instance.Hide();
        return false;
    }
}

[HarmonyPatch]
internal static class MultiplayerPauseMenuUnsafeActionsPatch
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(UI_POP_PauseMenu), nameof(UI_POP_PauseMenu.OnBtn_Save));
        yield return AccessTools.Method(typeof(UI_POP_PauseMenu), nameof(UI_POP_PauseMenu.OnBtn_Restart));
    }

    private static bool Prefix() => !InputGate.MultiplayerActive;
}

[HarmonyPatch(typeof(UI_Player_Info), nameof(UI_Player_Info.RenderCO))]
internal static class MultiplayerPlayerNamePatch
{
    private static void Postfix(UI_Player_Info __instance, Player ply)
    {
        if (!InputGate.MultiplayerActive || ply is null || ply.defeated || __instance.txt_player is null) return;
        var displayName = XingyiStarryMpPlugin.Instance?.GetSeatDisplayName(ply);
        if (!string.IsNullOrEmpty(displayName)) __instance.txt_player.text = displayName;
    }
}

[HarmonyPatch(typeof(UX_Manager), nameof(UX_Manager.SelectUnit))]
internal static class MultiplayerForeignSelectionPatch
{
    private static bool Prefix() => !InputGate.MultiplayerActive || XingyiStarryMpPlugin.Instance?.LocalOwnsCurrentTurn == true;
}

[HarmonyPatch(typeof(UI_Part_IdleButtons), "Update")]
internal static class MultiplayerIdleButtonsPatch
{
    private static void Postfix(UI_Part_IdleButtons __instance)
    {
        if (!InputGate.MultiplayerActive) return;
        var mayAct = InputGate.MaySubmit;
        if (__instance.btn_next_unit is not null) __instance.btn_next_unit.interactable &= mayAct;
        if (__instance.btn_auto_cmd is not null) __instance.btn_auto_cmd.interactable &= mayAct;
        if (__instance.btn_undo_move is not null) __instance.btn_undo_move.interactable &= mayAct;
        var endTurnRect = SingletonMono<SS_ANNW_Game>.self?.ui?.ping_manager?.rt_end_turn_btn;
        var endTurn = endTurnRect?.GetComponent<Button>() ?? endTurnRect?.GetComponentInChildren<Button>(true) ?? endTurnRect?.GetComponentInParent<Button>();
        if (endTurn is not null)
            endTurn.interactable = mayAct && !GS_Battle.self.functions.Querry(GAME_FUNCTION.NoEndTurn);
    }
}

[HarmonyPatch(typeof(UI_POP_AITurn), "Update")]
internal static class AiTurnSkipButtonsPatch
{
    private static void Postfix(UI_POP_AITurn __instance)
    {
        if (!InputGate.MultiplayerActive) return;
        if (__instance.btn_skip_cur is not null) __instance.btn_skip_cur.interactable = false;
        if (__instance.btn_skip_all is not null) __instance.btn_skip_all.interactable = false;
    }
}

[HarmonyPatch]
internal static class AiTurnSkipClickPatch
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(UI_POP_AITurn), nameof(UI_POP_AITurn.OnClick_Skip_Current));
        yield return AccessTools.Method(typeof(UI_POP_AITurn), nameof(UI_POP_AITurn.OnClick_Skip));
    }

    private static bool Prefix() => !InputGate.MultiplayerActive;
}

[HarmonyPatch(typeof(UI_Part_SkillPower), nameof(UI_Part_SkillPower.UpdateRender))]
internal static class MultiplayerCommanderSkillUiPatch
{
    private static void Prefix(ref Player? __state)
    {
        if (!InputGate.MultiplayerActive || GS_Battle.self is null) return;
        var local = XingyiStarryMpPlugin.Instance?.GetLocalDisplayPlayer();
        if (local is null) return;
        __state = GS_Battle.self.cur_player;
        GS_Battle.self.cur_player = local;
    }

    private static void Postfix(UI_Part_SkillPower __instance, Player? __state)
    {
        if (__state is not null && GS_Battle.self is not null) GS_Battle.self.cur_player = __state;
        if (!InputGate.MultiplayerActive || __instance.skillBtn is null) return;
        var group = __instance.skillBtn.GetComponent<UnityEngine.CanvasGroup>() ?? __instance.skillBtn.gameObject.AddComponent<UnityEngine.CanvasGroup>();
        group.alpha = InputGate.MaySubmit ? 1f : 0.55f;
        group.blocksRaycasts = InputGate.MaySubmit;
        group.interactable = InputGate.MaySubmit;
    }
}

[HarmonyPatch(typeof(UI_SkillBtn), nameof(UI_SkillBtn.OnClick))]
internal static class MultiplayerCommanderSkillClickPatch
{
    private static bool Prefix() => !InputGate.MultiplayerActive || InputGate.MaySubmit;
}

[HarmonyPatch(typeof(FUI_QuickUndo), "Update")]
internal static class MultiplayerQuickUndoHintPatch
{
    private static void Postfix(FUI_QuickUndo __instance)
    {
        if (InputGate.MultiplayerActive && !InputGate.LocalSeatMayAct && __instance.sr is not null)
            __instance.sr.gameObject.SetActive(false);
    }
}

[HarmonyPatch(typeof(FUI_TargetHint), "Update")]
internal static class MultiplayerTargetHintPatch
{
    private static void Postfix(FUI_TargetHint __instance)
    {
        if (InputGate.MultiplayerActive && __instance.rendering_part is not null)
            __instance.rendering_part.SetActive(InputGate.LocalSeatMayAct);
    }
}
