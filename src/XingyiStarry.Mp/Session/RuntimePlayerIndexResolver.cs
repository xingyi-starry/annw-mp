using System;
using System.Collections.Generic;

namespace XingyiStarry.Mp.Session;

internal static class RuntimePlayerIndexResolver
{
    public static int[] Build(IReadOnlyList<bool> existingLobbySlots)
    {
        if (existingLobbySlots is null) throw new ArgumentNullException(nameof(existingLobbySlots));
        var result = new int[existingLobbySlots.Count];
        var runtimeIndex = 0;
        for (var lobbySlotIndex = 0; lobbySlotIndex < existingLobbySlots.Count; lobbySlotIndex++)
            result[lobbySlotIndex] = existingLobbySlots[lobbySlotIndex] ? runtimeIndex++ : -1;
        return result;
    }
}
