using XingyiStarry.Mp.Session;
using XingyiStarry.Mp.Ui;

namespace XingyiStarry.Mp.Game;

internal static class InputGate
{
    public static bool MultiplayerActive { get; set; }
    public static bool LocalSeatMayAct { get; set; }
    public static bool TextInputCaptured => ChatWindow.IsTyping;
    public static bool PointerOverChat => ChatWindow.IsPointerOverNow;
    public static bool ShouldRunOriginal => !MultiplayerActive || ExecutionContext.IsAuthoritativeExecution;
    public static bool MaySubmit => MultiplayerActive && LocalSeatMayAct && !ExecutionContext.IsAuthoritativeExecution;
}
