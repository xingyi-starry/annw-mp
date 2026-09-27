using ANNW;
using HarmonyLib;

namespace XingyiStarry.Mp.Game;

internal static class SpectatorFeature
{
    private static GS_Battle? owner;
    private static FOWMap? omniscient;

    internal static void Tick(XingyiStarryMpPlugin plugin)
    {
        if (!InputGate.MultiplayerActive || !plugin.IsSpectator || GS_Battle.self is null) return;
        GS_Battle.self.last_human_player = null;
        GS_Battle.self.is_player_in_control = false;
        var mouse = SingletonMono<SS_ANNW_Game>.self?.mouse_input;
        if (mouse is not null) mouse.free_mode = true;
    }

    internal static FOWMap? GetOmniscient()
    {
        var battle = GS_Battle.self;
        if (battle is null) return null;
        if (owner == battle && omniscient is not null && omniscient.map.Count == battle.terrain.dic_map.Count) return omniscient;
        owner = battle;
        omniscient = new FOWMap { parent = battle.fow_maps };
        omniscient.Init(Fraction.NEUTRAL);
        foreach (var tile in omniscient.map.Values)
        {
            tile.detected = true; tile.visible = true; tile.state = FOWState.SEEN;
        }
        return omniscient;
    }

    internal static void Reset() { owner = null; omniscient = null; }
}

[HarmonyPatch(typeof(GS_Battle), "GetDisplayFOWMap")]
internal static class SpectatorDisplayFowPatch
{
    private static void Postfix(ref FOWMap __result)
    {
        if (Session.ExecutionContext.IsAuthoritativeExecution || XingyiStarryMpPlugin.Instance?.IsSpectator != true) return;
        __result = SpectatorFeature.GetOmniscient() ?? __result;
    }
}

[HarmonyPatch(typeof(ANNW_MouseInput), nameof(ANNW_MouseInput.ResetFreeModeAtSequenceStart))]
internal static class SpectatorFreeCameraPatch
{
    private static void Postfix(ANNW_MouseInput __instance)
    {
        if (XingyiStarryMpPlugin.Instance?.IsSpectator == true) __instance.free_mode = true;
    }
}
