using System;
using System.Text;
using UnityEngine.SceneManagement;

namespace XingyiStarry.Mp.Game;

internal static class GameSnapshotLoader
{
    public static void Load(byte[] snapshot)
    {
        var parsed = DynOb.Parse(Encoding.UTF8.GetString(snapshot)) as DynOb;
        if (parsed is null) throw new InvalidOperationException("Snapshot root is not a DynOb.");
        if (!parsed.HasKey("startGameSetting") || !parsed.HasKey("terrain") || !parsed.HasKey("commander") || !parsed.HasKey("units"))
            throw new InvalidOperationException("Snapshot is missing required full-game sections.");
        SS_ANNW_Game.start_game_setting = new StartGameSetting
        {
            game_type = GameType.LOAD_GAME,
            ob_file = parsed,
            filename = "XingyiStarryMpSnapshot",
            is_new = false,
            file_path = ""
        };
        SceneManager.LoadScene("ANNW_Battle");
    }
}
