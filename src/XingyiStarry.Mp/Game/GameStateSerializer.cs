using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Game;

internal static class GameStateSerializer
{
    private static readonly HashSet<string> LocalRootKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_game_time", "play_time", "current_bgm_key", "show_border"
    };

    public static byte[] CaptureSnapshot() => Encoding.UTF8.GetBytes(GS_Battle.self.Save_General(false).ToString());

    public static byte[] ComputeStateHash()
    {
        using var writer = new CanonicalWriter();
        WriteDynOb(writer, GS_Battle.self.Save_General(false), true);
        using var sha = SHA256.Create(); return sha.ComputeHash(writer.ToArray());
    }

    private static void WriteDynOb(CanonicalWriter writer, DynOb value, bool root)
    {
        writer.Write((byte)1);
        var keys = new List<string>(value.dic_values.Keys);
        if (root) keys.RemoveAll(LocalRootKeys.Contains);
        keys.Sort(StringComparer.Ordinal); writer.Write(keys.Count);
        foreach (var key in keys) { writer.Write(key); WriteValue(writer, value.dic_values[key]); }
    }

    private static void WriteValue(CanonicalWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.Write((byte)0); break;
            case DynOb dyn: WriteDynOb(writer, dyn, false); break;
            case string text: writer.Write((byte)2); writer.Write(text); break;
            case bool boolean: writer.Write((byte)3); writer.Write(boolean); break;
            case int integer: writer.Write((byte)4); writer.Write(integer); break;
            case long longValue: writer.Write((byte)5); writer.Write(longValue); break;
            case float single: writer.Write((byte)6); writer.Write(BitConverter.GetBytes(single)); break;
            case double number: writer.Write((byte)7); writer.Write(number); break;
            case Enum enumeration: writer.Write((byte)8); writer.Write(enumeration.GetType().FullName ?? enumeration.GetType().Name); writer.Write(Convert.ToInt64(enumeration, CultureInfo.InvariantCulture)); break;
            case Inctor2 position: writer.Write((byte)9); writer.Write(position.x); writer.Write(position.y); break;
            case Vector2 vector: writer.Write((byte)10); writer.Write(BitConverter.GetBytes(vector.x)); writer.Write(BitConverter.GetBytes(vector.y)); break;
            case Vector3 vector: writer.Write((byte)11); writer.Write(BitConverter.GetBytes(vector.x)); writer.Write(BitConverter.GetBytes(vector.y)); writer.Write(BitConverter.GetBytes(vector.z)); break;
            case Color color: writer.Write((byte)12); writer.Write(BitConverter.GetBytes(color.r)); writer.Write(BitConverter.GetBytes(color.g)); writer.Write(BitConverter.GetBytes(color.b)); writer.Write(BitConverter.GetBytes(color.a)); break;
            case IList list:
                writer.Write((byte)13); writer.Write(list.Count);
                foreach (var item in list) WriteValue(writer, item);
                break;
            default:
                writer.Write((byte)255); writer.Write(value.GetType().FullName ?? value.GetType().Name); writer.Write(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }
}
