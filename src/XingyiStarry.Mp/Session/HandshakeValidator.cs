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
            reason = "${notice_version_or_content_fingerprint_mismatch}";
            return false;
        }
        var suppliedPassword = hello.Password ?? "";
        if (suppliedPassword.Length > MaxPasswordCharacters ||
            !string.Equals(suppliedPassword, expectedPassword ?? "", StringComparison.Ordinal))
        {
            reason = "${notice_incorrect_password}";
            return false;
        }
        if (displayName.Length == 0 || displayName.Length > 32)
        {
            reason = "${ui_username_must_contain_1_32_characters}";
            return false;
        }
        reason = "";
        return true;
    }
}
