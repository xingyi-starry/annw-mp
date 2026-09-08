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
    private static bool Prefix(ref IEnumerator __result)
    {
        if (InputGate.ShouldRunOriginal) return true;
        XingyiStarryMpPlugin.Instance?.SubmitCommand(new Protocol.GameCommand { Kind = Protocol.CommandKind.EndTurn });
        __result = Empty(); return false;
    }

    private static IEnumerator Empty() { yield break; }
}

[HarmonyPatch(typeof(UnitAI), nameof(UnitAI.ExecuteAction))]
internal static class UnitAiPatch
{
    private static bool Prefix() => InputGate.ShouldRunOriginal;
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
