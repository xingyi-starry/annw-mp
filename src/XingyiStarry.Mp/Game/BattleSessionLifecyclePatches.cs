using HarmonyLib;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(SS_ANNW_Game), nameof(SS_ANNW_Game.StartGameFormal))]
internal static class MultiplayerBattleStartPatch
{
    private static void Postfix() => XingyiStarryMpPlugin.Instance?.OnNativeStartGameFormal();
}

[HarmonyPatch(typeof(SS_ANNW_Game), nameof(SS_ANNW_Game.LeaveGame))]
internal static class MultiplayerBattleLeavePatch
{
    private static void Postfix() => XingyiStarryMpPlugin.Instance?.OnNativeBattleLeft();
}

[HarmonyPatch(typeof(GS_Battle), "ResumeGame")]
internal static class MultiplayerLoadedBattleResumePatch
{
    private static void Postfix() => XingyiStarryMpPlugin.Instance?.OnNativeLoadedGameResumed();
}
