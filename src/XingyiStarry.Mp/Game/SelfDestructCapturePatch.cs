using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

[HarmonyPatch(typeof(GameAPI), nameof(GameAPI.DoSelfDestruct))]
internal static class SelfDestructCapturePatch
{
    private static bool Prefix(GameAPI __instance, UnitData u)
    {
        if (InputGate.ShouldRunOriginal) return true;
        if (!InputGate.MaySubmit || u is null || GS_Battle.self is null ||
            u.player != GS_Battle.self.cur_player || u.dying || !__instance.CanDoSelfDestruct(u)) return false;

        UI_Floater.self.pop_general.ShowAsGeneral(LAN.Get("UI_ConfirmSelfDestruct"), () =>
        {
            if (!InputGate.MaySubmit || GS_Battle.self is null ||
                u.player != GS_Battle.self.cur_player || u.dying || !__instance.CanDoSelfDestruct(u)) return;
            XingyiStarryMpPlugin.Instance?.SubmitCommand(new GameCommand
            {
                Kind = CommandKind.SelfDestruct,
                UnitIds = new long[] { u.unit_id }
            });
        });
        return false;
    }
}
