using XingyiStarry.Mp.Session;

namespace XingyiStarry.Mp.Game;

internal static class InputGate
{
    public static bool MultiplayerActive { get; set; }
    public static bool LocalSeatMayAct { get; set; }
    public static bool ShouldRunOriginal => !MultiplayerActive || ExecutionContext.IsAuthoritativeExecution;
    public static bool MaySubmit => MultiplayerActive && LocalSeatMayAct && !ExecutionContext.IsAuthoritativeExecution;
}
