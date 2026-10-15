using System;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal static class HandshakeValidator
{
    public const int MaxPasswordCharacters = 64;

    public static bool TryValidate(HelloMessage hello, string pluginVersion, string gameFingerprint,
        string contentFingerprint, string expectedPassword, out string displayName, out string reason)
    {
        displayName = (hello.DisplayName ?? "").Trim();
        if (hello.ProtocolVersion != ProtocolConstants.Version || hello.PluginVersion != pluginVersion ||
            hello.GameFingerprint != gameFingerprint || hello.ContentFingerprint != contentFingerprint)
        {
            reason = "Version or content fingerprint mismatch.";
            return false;
        }
        var suppliedPassword = hello.Password ?? "";
        if (suppliedPassword.Length > MaxPasswordCharacters ||
            !string.Equals(suppliedPassword, expectedPassword ?? "", StringComparison.Ordinal))
        {
            reason = "房间密码错误。";
            return false;
        }
        if (displayName.Length == 0 || displayName.Length > 32)
        {
            reason = "用户名长度必须为 1–32 个字符。";
            return false;
        }
        reason = "";
        return true;
    }
}
