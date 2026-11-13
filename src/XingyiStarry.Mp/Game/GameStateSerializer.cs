using System.Text;

namespace XingyiStarry.Mp.Game;

internal static class GameStateSerializer
{
    internal static bool CapturingSnapshot { get; private set; }
    public static byte[] CaptureSnapshot()
    {
        var previous = CapturingSnapshot;
        CapturingSnapshot = true;
        try { return Encoding.UTF8.GetBytes(GS_Battle.self.Save_General(false).ToString()); }
        finally { CapturingSnapshot = previous; }
    }
}
