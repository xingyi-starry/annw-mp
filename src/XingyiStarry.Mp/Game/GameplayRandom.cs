using System;
using System.Collections.Generic;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

internal static class GameplayRandom
{

    public static float Range(float minimum, float maximum, string callSite)
    {
        var result = Protocol.RandomResultContext.Current;
        if (result is null && AttackSettlements.Current is not null) throw new System.InvalidOperationException("Gameplay random has no settlement owner: " + callSite);
        return result is null ? UnityEngine.Random.Range(minimum, maximum) : result.Float(callSite, () => UnityEngine.Random.Range(minimum, maximum));
    }

    public static int Range(int minimum, int maximum, string callSite)
    {
        var result = Protocol.RandomResultContext.Current;
        if (result is null && AttackSettlements.Current is not null) throw new System.InvalidOperationException("Gameplay random has no settlement owner: " + callSite);
        return result is null ? UnityEngine.Random.Range(minimum, maximum) : result.Integer(callSite, () => UnityEngine.Random.Range(minimum, maximum));
    }

    public static float UnitDataHurtFloat(float minimum, float maximum) => Range(minimum, maximum, "UnitData.Hurt");
    public static int UnitDataHurtInt(int minimum, int maximum) => Range(minimum, maximum, "UnitData.Hurt");
    public static float UnitDataDieFloat(float minimum, float maximum) => Range(minimum, maximum, "UnitData.Die");
    public static int UnitDataDieInt(int minimum, int maximum) => Range(minimum, maximum, "UnitData.Die");
    public static float GameTileDataCreateWreckFloat(float minimum, float maximum) => Range(minimum, maximum, "GameTileData.CreateWreck");
    public static int GameTileDataCreateWreckInt(int minimum, int maximum) => Range(minimum, maximum, "GameTileData.CreateWreck");
    public static float PlayerAutoSetCmdPosFloat(float minimum, float maximum) => Range(minimum, maximum, "Player.AutoSetCmdPos");
    public static int PlayerAutoSetCmdPosInt(int minimum, int maximum) => Range(minimum, maximum, "Player.AutoSetCmdPos");

    public static void ShuffleInctor2(IList<Inctor2> list)
    {
        for (var remaining = list.Count; remaining > 1;)
        {
            remaining--;
            var index = Range(0, remaining + 1, "UnitEffect_ShareExp.PreAddExp/Shuffle");
            var value = list[index]; list[index] = list[remaining]; list[remaining] = value;
        }
    }
}
