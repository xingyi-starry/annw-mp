using System;
using System.Collections.Generic;
using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

internal static class GameplayRandom
{
    public static RandomTape? ActiveTape { get; set; }

    public static float Range(float minimum, float maximum, string callSite)
    {
        var tape = ActiveTape;
        return tape is null ? UnityEngine.Random.Range(minimum, maximum) : tape.Float(callSite, () => UnityEngine.Random.Range(minimum, maximum));
    }

    public static int Range(int minimum, int maximum, string callSite)
    {
        var tape = ActiveTape;
        return tape is null ? UnityEngine.Random.Range(minimum, maximum) : tape.Integer(callSite, () => UnityEngine.Random.Range(minimum, maximum));
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
