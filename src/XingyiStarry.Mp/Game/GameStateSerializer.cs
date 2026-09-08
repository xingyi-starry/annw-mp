using System.Text;

namespace XingyiStarry.Mp.Game;

internal static class GameStateSerializer
{
    public static byte[] CaptureSnapshot() => Encoding.UTF8.GetBytes(GS_Battle.self.Save_General(false).ToString());
}
