using System;
using System.Collections.Generic;
using ANNW;
using HarmonyLib;
using UnityEngine;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

internal static class PingFeature
{
    private static readonly List<Marker> markers = new List<Marker>();
    private static Material? material;

    internal static void Tick(XingyiStarryMpPlugin plugin)
    {
        UpdateMarkers();
        if (!InputGate.MultiplayerActive || InputGate.TextInputCaptured || plugin.IsSpectator || GS_Battle.self?.game_running != true || GS_Battle.self.is_movie_mode) return;
        if (InputKeybinding.IsTextInputActive() || UI_POP_KeyBinding.is_capturing || SUI_DBG_BATTLE.IsOpen()) return;
        if (StackableUIManager.self != null && StackableUIManager.self.HasActiveUI) return;
        if (GG.IsMouseOverUI()) return;
        if (!InputKeybinding.IsActionKeyPressed(NativePingKeyBinding.Action)) return;
        TrySend(plugin, FUI_WorldCursor.HoverTile);
    }

    internal static void TrySend(XingyiStarryMpPlugin plugin, Inctor2 tile)
    {
        if (!GameTileData.IsValid(tile)) return;
        plugin.SendPing(tile.x, tile.y);
    }

    internal static void Show(PingEvent message)
    {
        if (GS_Battle.self?.game_running != true) return;
        var tile = new Inctor2(message.TileX, message.TileY); if (!GameTileData.IsValid(tile)) return;
        var go = new GameObject("XingyiStarryMp_Ping");
        var line = go.AddComponent<LineRenderer>(); line.useWorldSpace = true; line.loop = true; line.positionCount = 24;
        line.startWidth = line.endWidth = 0.09f; line.numCornerVertices = 2; line.numCapVertices = 2;
        material ??= new Material(Shader.Find("Sprites/Default")); line.sharedMaterial = material;
        var color = DataUtils.COColorToUnityColorUI((COColor)Mathf.Clamp(message.SenderColor, 0, 8));
        line.startColor = line.endColor = color;
        var center = (Vector3)(AccessTools.Method(typeof(SS_ANNW_Game), "GetWP", new[] { typeof(Inctor2) })?.Invoke(null, new object[] { tile }) ?? Vector3.zero);
        center.y = Mathf.Max(0f, GameTileData.Get(tile).sd_terrain.unit_pos) + 0.24f;
        for (var index = 0; index < line.positionCount; index++)
        {
            var angle = Mathf.PI * 2f * index / line.positionCount;
            line.SetPosition(index, center + new Vector3(Mathf.Cos(angle) * 0.62f, 0f, Mathf.Sin(angle) * 0.62f));
        }
        SFX.PlaySD("PingHint");
        markers.Add(new Marker(go, line, Time.unscaledTime + Math.Max(0.1f, message.RemainingTtlMillis / 1000f)));
    }

    internal static void Reset()
    {
        foreach (var marker in markers) if (marker.Root != null) UnityEngine.Object.Destroy(marker.Root);
        markers.Clear();
    }

    private static void UpdateMarkers()
    {
        for (var index = markers.Count - 1; index >= 0; index--)
        {
            var marker = markers[index];
            if (marker.Root == null || Time.unscaledTime >= marker.Expires)
            { if (marker.Root != null) UnityEngine.Object.Destroy(marker.Root); markers.RemoveAt(index); continue; }
            var remaining = marker.Expires - Time.unscaledTime;
            var pulse = 0.65f + Mathf.Sin(Time.unscaledTime * 7f) * 0.18f;
            marker.Root.transform.localScale = Vector3.one * pulse;
            var color = marker.Line.startColor; color.a = Mathf.Clamp01(remaining); marker.Line.startColor = marker.Line.endColor = color;
        }
    }

    private sealed class Marker
    {
        public GameObject Root { get; }
        public LineRenderer Line { get; }
        public float Expires { get; }
        public Marker(GameObject root, LineRenderer line, float expires) { Root = root; Line = line; Expires = expires; }
    }
}

[HarmonyPatch(typeof(UI_POP_QuickMenu), "ShowItems")]
internal static class MultiplayerPingQuickMenuPatch
{
    private static readonly AccessTools.FieldRef<UI_POP_QuickMenu, Inctor2> OpenedTile =
        AccessTools.FieldRefAccess<UI_POP_QuickMenu, Inctor2>("opened_tile_inc");

    private static void Prefix(UI_POP_QuickMenu __instance)
    {
        var plugin = XingyiStarryMpPlugin.Instance;
        if (plugin is null || !InputGate.MultiplayerActive || plugin.IsSpectator || GS_Battle.self?.game_running != true ||
            __instance.stacks.Count == 0 || __instance.stacks.Peek() != "root") return;
        var tile = OpenedTile(__instance); if (!GameTileData.IsValid(tile)) return;
        __instance.items.Add(new QuickMenuItem { name = "标记此处", interactable = true, auto_close = true,
            cb = () => PingFeature.TrySend(plugin, tile) });
    }
}
