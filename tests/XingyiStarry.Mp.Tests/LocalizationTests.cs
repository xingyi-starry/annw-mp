using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Tests;

internal static class LocalizationTests
{
    internal static void Rendering()
    {
        Equal("", MpText.Render("", true));
        Equal("raw details / 玩家 / 123", MpText.Render("raw details / 玩家 / 123", true));
        Equal("${unknown_key}", MpText.Render("${unknown_key}", true));
        Equal("玩家 Alice加入了房间。", MpText.Render("玩家 Alice${notice_joined}", true));
        Equal("玩家 Alice joined the room.", MpText.Render("玩家 Alice${notice_joined}", false));
        Equal("用户名 / 密码 / 用户名", MpText.Render("${ui_username} / ${ui_password} / ${ui_username}", true));
        Equal("Username / Password / Username", MpText.Render("${ui_username} / ${ui_password} / ${ui_username}", false));
        Equal("正在连接 127.0.0.1:24555", MpText.Render("${status_connecting}127.0.0.1:24555", true));
    }

    internal static void LanguageSwitch()
    {
        MpText.SetLanguage(false);
        var changes = 0;
        void Changed() => changes++;
        MpText.LanguageChanged += Changed;
        try
        {
            const string history = "Alice${notice_joined}";
            Equal("Alice joined the room.", MpText.Render(history));
            MpText.SetLanguage(true);
            Equal("Alice加入了房间。", MpText.Render(history));
            MpText.SetLanguage(true);
            Equal(1, changes);
            MpText.SetLanguage(false);
            Equal("Alice joined the room.", MpText.Render(history));
            Equal(2, changes);
            Equal("Alice${notice_joined}", history);
        }
        finally { MpText.LanguageChanged -= Changed; MpText.SetLanguage(false); }
    }

    internal static void WireRoundTrips()
    {
        const string rejection = "${notice_incorrect_password}";
        Equal(rejection, ProtocolCodec.DecodeString(ProtocolCodec.EncodeString(rejection)));
        var command = ProtocolCodec.DecodeCommandResponse(ProtocolCodec.EncodeCommandResponse(
            new CommandResponse { RequestId = 12, Reason = rejection }));
        Equal(rejection, command.Reason);
        var failed = ProtocolCodec.DecodeOperationFailed(ProtocolCodec.EncodeOperationFailed(
            new OperationFailedPayload { OperationId = Guid.NewGuid(), Reason = rejection }));
        Equal("房间密码错误。", MpText.Render(failed.Reason, true));
        Equal("Incorrect room password.", MpText.Render(failed.Reason, false));
        var message = ProtocolCodec.DecodeChatEvent(ProtocolCodec.EncodeChatEvent(
            new ChatEvent { Kind = ChatKind.System, SystemKind = SystemEventKind.Joined, Text = "Alice${notice_joined}" }));
        Equal("Alice${notice_joined}", message.Text);
        Equal("Alice加入了房间。", MpText.Render(message.Text, true));
        Equal("Alice joined the room.", MpText.Render(message.Text, false));
    }

    internal static void CatalogCoverage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "XingyiStarry.Mp.sln"))) directory = directory.Parent;
        if (directory == null) throw new Exception("Repository source is required for the catalog coverage check.");
        var sources = Directory.GetFiles(Path.Combine(directory.FullName, "src", "XingyiStarry.Mp"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
        var keys = sources.SelectMany(path => Regex.Matches(string.Join("\n", File.ReadLines(path)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))), @"\$\{\{?([a-z0-9_]+)\}").Cast<Match>())
            .Select(match => match.Groups[1].Value).Distinct();
        foreach (var key in keys)
        {
            var token = "${" + key + "}";
            foreach (var chinese in new[] { true, false })
            {
                var rendered = MpText.Render(token, chinese);
                if (rendered == token || rendered.Length == 0 || rendered.Contains("${", StringComparison.Ordinal))
                    throw new Exception($"Missing or invalid {(chinese ? "Chinese" : "English")} translation: {key}");
            }
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
    }
}
