using System.Collections;
using ANNW;
using HarmonyLib;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(GameController), nameof(GameController.ExecuteAction))]
internal static class ExecuteActionPatch
{
    private static bool Prefix(UnitData unit, ActionCate cate, GameTileData target, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new Protocol.GameCommand { Kind = Protocol.CommandKind.Action, UnitIds = new long[] { unit.unit_id }, TargetX = target.pos.x, TargetY = target.pos.y, ActionCategory = (int)cate });
        __result = Empty(); return false;
    }
    private static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(UndoMoveData), nameof(UndoMoveData.UndoLastMove))]
internal static class UndoMovePatch
{
    private static bool Prefix()
    {
        if (InputGate.ShouldRunOriginal) return true;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new Protocol.GameCommand { Kind = Protocol.CommandKind.UndoMove });
        return false;
    }
}

[HarmonyPatch(typeof(GameController), nameof(GameController.EndPlayerTurn))]
internal static class EndTurnPatch
{
    private static bool Prefix(Player player, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        var plugin = XingyiStarryMpPlugin.Instance;
        if (player.is_ai && plugin?.ShouldCaptureHostAi == true)
        {
            __result = plugin.RunHostAiEndTurn();
            return false;
        }
        if (player.is_ai && plugin?.ShouldSuppressClientAi == true)
        {
            __result = Empty();
            return false;
        }
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new Protocol.GameCommand { Kind = Protocol.CommandKind.EndTurn });
        __result = Empty(); return false;
    }

    private static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(UnitAI), nameof(UnitAI.ExecuteAction))]
internal static class UnitAiPatch
{
    private static bool Prefix(UnitAI __instance, UtilityItem ut, bool skipping, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        var plugin = XingyiStarryMpPlugin.Instance;
        if (plugin?.ShouldCaptureHostAi == true && __instance.owner?.player == GS_Battle.self.cur_player)
            __result = plugin.RunHostAiCommand(XingyiStarryMpPlugin.CreateAiUnitCommand(__instance, ut, skipping));
        else
            __result = Empty();
        return false;
    }

    private static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(PlayerAI), nameof(PlayerAI.OnStartTurn_DoTurn))]
internal static class PlayerAiTurnPatch
{
    private static bool Prefix(PlayerAI __instance, ref IEnumerator __result)
    {
        if (Session.ExecutionContext.SuppressAiDecision)
        {
            __result = Empty();
            return false;
        }
        if (XingyiStarryMpPlugin.Instance?.ShouldSuppressClientAi != true) return true;
        __result = XingyiStarryMpPlugin.WaitForAuthorityAiTurnEnd(__instance.owner.index);
        return false;
    }

    private static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(CO_Data), nameof(CO_Data.proc_CastSkill))]
internal static class AiSkillPatch
{
    private static bool Prefix(GameTileData target, bool skipping, ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal || XingyiStarryMpPlugin.Instance?.ShouldCaptureHostAi != true) return true;
        __result = XingyiStarryMpPlugin.Instance.RunHostAiCommand(new Protocol.GameCommand
        {
            Kind = Protocol.CommandKind.AiSkill, TargetX = target.pos.x, TargetY = target.pos.y,
            DesiredToggleState = skipping
        });
        return false;
    }
}

[HarmonyPatch(typeof(GameController), nameof(GameController.StartPlayerTurn))]
internal static class StartPlayerTurnPatch
{
    private static void Prefix(Player player) => XingyiStarryMpPlugin.Instance?.OnSeatTurnStarting(player.index);

    private static void Postfix(Player player, ref IEnumerator __result)
    {
        if (__result != null) __result = Wrap(__result, player.index);
    }

    private static IEnumerator Wrap(IEnumerator original, int playerIndex)
    {
        while (original.MoveNext()) yield return original.Current;
        XingyiStarryMpPlugin.Instance?.OnNativeStartPlayerTurnCompleted(playerIndex);
    }
}

[HarmonyPatch(typeof(GS_Battle), nameof(GS_Battle.GetDisplayPlayer))]
internal static class MultiplayerDisplayPlayerPatch
{
    private static void Postfix(ref Player __result)
    {
        var local = XingyiStarryMpPlugin.Instance?.GetLocalDisplayPlayer();
        if (local is not null) __result = local;
    }
}

[HarmonyPatch]
internal static class MultiplayerWorldInputPatch
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ANNW_MouseInput), nameof(ANNW_MouseInput.OnClick));
        yield return AccessTools.Method(typeof(ANNW_MouseInput), nameof(ANNW_MouseInput.OnDoubleClick));
        yield return AccessTools.Method(typeof(ANNW_MouseInput), nameof(ANNW_MouseInput.OnStartDrag));
        yield return AccessTools.Method(typeof(ANNW_MouseInput), nameof(ANNW_MouseInput.OnEndDrag));
    }
    private static bool Prefix() => !InputGate.MultiplayerActive || InputGate.MaySubmit;
}

[HarmonyPatch(typeof(SS_ANNW_Game), "Surrender")]
internal static class MultiplayerSurrenderPatch
{
    private static bool Prefix()
    {
        if (!InputGate.MultiplayerActive) return true;
        XingyiStarryMpPlugin.Instance?.SubmitSurrender();
        return false;
    }
}

[HarmonyPatch(typeof(SS_ANNW_Game), "EndGame")]
internal static class MultiplayerMatchEndPatch
{
    private static bool Prefix(bool victory) => XingyiStarryMpPlugin.Instance?.InterceptNativeMatchEnd(victory) ?? true;
}
