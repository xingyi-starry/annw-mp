using System;
using System.Collections.Generic;
using System.Linq;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal sealed class RoomState
{
    private readonly List<SeatInfo> seats = new List<SeatInfo>();
    public Guid RoomId { get; }
    public IReadOnlyList<SeatInfo> Seats => seats;
    public bool MatchStarted { get; private set; }
    public int DraftRevision { get; private set; }
    public string MapId { get; private set; } = "";
    public string MapTitle { get; private set; } = "";
    public int FowType { get; private set; }
    public int WinCondition { get; private set; }
    public int QuickStart { get; private set; }
    public bool SavedGame { get; private set; }
    public bool UserMap { get; private set; }
    public byte[] MapPreview { get; private set; } = Array.Empty<byte>();
    private string draftFingerprint = "";

    public RoomState(Guid? roomId = null) => RoomId = roomId ?? Guid.NewGuid();

    public void ReplaceSeats(IEnumerable<SeatInfo> values)
    {
        if (MatchStarted) throw new InvalidOperationException("Cannot replace seats after match start.");
        seats.Clear(); seats.AddRange(values); ClearReady();
    }

    public void ConfigureSavedGame(string mapId, string mapTitle, int fowType, int winCondition, int quickStart, IEnumerable<SeatInfo> values)
    {
        if (MatchStarted) throw new InvalidOperationException("Cannot configure a saved game after match start.");
        SavedGame = true; UserMap = false; MapPreview = Array.Empty<byte>(); MapId = mapId; MapTitle = mapTitle; FowType = fowType; WinCondition = winCondition; QuickStart = quickStart;
        seats.Clear(); seats.AddRange(values); ClearReady(); DraftRevision++;
    }

    public bool SyncDraft(string mapId, string mapTitle, int fowType, int winCondition, int quickStart, bool userMap, byte[] mapPreview, string fingerprint, IEnumerable<SeatInfo> values)
    {
        if (MatchStarted) return false;
        var desired = values.OrderBy(value => value.LobbySlotIndex).ToList();
        var changed = MapId != mapId || MapTitle != mapTitle || FowType != fowType || WinCondition != winCondition || QuickStart != quickStart ||
            UserMap != userMap || !MapPreview.SequenceEqual(mapPreview) || draftFingerprint != fingerprint ||
            seats.Count != desired.Count || seats.Zip(desired, (left, right) => left.LobbySlotIndex != right.LobbySlotIndex || left.OriginallyHuman != right.OriginallyHuman).Any(value => value);
        if (!changed) return false;

        var old = seats.ToDictionary(value => (value.LobbySlotIndex, value.OriginallyHuman));
        seats.Clear();
        foreach (var item in desired)
        {
            if (old.TryGetValue((item.LobbySlotIndex, item.OriginallyHuman), out var existing) && existing.Connected)
            {
                item.SeatId = existing.SeatId; item.ClientId = existing.ClientId; item.DisplayName = existing.DisplayName;
                item.Connected = true; item.AiControlled = false;
            }
            item.Ready = !item.OriginallyHuman;
            seats.Add(item);
        }
        MapId = mapId; MapTitle = mapTitle; FowType = fowType; WinCondition = winCondition; QuickStart = quickStart;
        UserMap = userMap; MapPreview = (byte[])mapPreview.Clone(); draftFingerprint = fingerprint;
        DraftRevision++;
        return true;
    }

    public bool TryClaimSeat(Guid clientId, string displayName, int lobbySlotIndex, out SeatInfo? claimed, out string reason)
    {
        claimed = seats.FirstOrDefault(s => s.LobbySlotIndex == lobbySlotIndex && (SavedGame || MatchStarted || s.OriginallyHuman));
        if (claimed is null) { reason = "目标不是可选择的席位。"; return false; }
        if (claimed.Defeated) { reason = "战败席位不能接管。"; return false; }
        if (claimed.Connected && claimed.ClientId != clientId) { reason = "该席位已被其他玩家占用。"; return false; }
        var previous = seats.FirstOrDefault(s => s.ClientId == clientId && s.Connected);
        if (previous is not null && previous != claimed)
        {
            Release(previous);
        }
        claimed.ClientId = clientId; claimed.DisplayName = displayName; claimed.Connected = true;
        if (previous != claimed) claimed.Ready = false;
        claimed.PendingActivation = MatchStarted;
        claimed.AiControlled = MatchStarted;
        reason = ""; return true;
    }

    public bool ReleaseSeat(Guid clientId)
    {
        var seat = seats.FirstOrDefault(s => s.ClientId == clientId && s.Connected);
        if (seat is null) return false;
        Release(seat); return true;
    }

    public void SetReady(Guid clientId, bool ready)
    {
        var seat = seats.Single(s => s.ClientId == clientId && s.Connected);
        seat.Ready = ready;
    }

    public bool CanStart => SavedGame
        ? !string.IsNullOrEmpty(MapId) && seats.Any(s => s.Connected && !s.Defeated) && seats.Where(s => s.Connected).All(s => s.Ready)
        : !string.IsNullOrEmpty(MapId) && seats.Any(s => s.OriginallyHuman) && seats.Where(s => s.OriginallyHuman).All(s => s.Connected && s.Ready);
    public void Start() { if (!CanStart) throw new InvalidOperationException("Not all connected human seats are ready."); MatchStarted = true; }
    public void ClearReady() { foreach (var seat in seats) seat.Ready = false; }

    public void BindRuntimePlayerIndices(IReadOnlyList<SGS_Player> players)
    {
        if (MatchStarted) throw new InvalidOperationException("Cannot bind player indices after match start.");
        if (SavedGame)
        {
            if (seats.Any(value => value.PlayerIndex < 0)) throw new InvalidOperationException("Saved-game seats are missing runtime indices.");
            return;
        }
        // SetupForSkirmish compacts enabled settings in lobby-row order; pos_ind only selects the map spawn.
        var runtimeIndices = RuntimePlayerIndexResolver.Build(players.Select(value => value.exist).ToArray());
        if (runtimeIndices.Count(value => value >= 0) != seats.Count)
            throw new InvalidOperationException("Lobby seats do not match the active start settings.");
        foreach (var seat in seats)
        {
            if (seat.LobbySlotIndex < 0 || seat.LobbySlotIndex >= players.Count) throw new InvalidOperationException("Lobby slot is outside the start settings.");
            seat.PlayerIndex = runtimeIndices[seat.LobbySlotIndex];
            if (seat.PlayerIndex < 0) throw new InvalidOperationException("Lobby seat refers to a disabled start setting.");
        }
        if (seats.Select(value => value.PlayerIndex).Distinct().Count() != seats.Count) throw new InvalidOperationException("Resolved player indices are not unique.");
    }

    public RoomSnapshot Snapshot()
    {
        var snapshot = new RoomSnapshot { RoomId = RoomId, MatchStarted = MatchStarted, DraftRevision = DraftRevision, MapId = MapId, MapTitle = MapTitle, FowType = FowType, WinCondition = WinCondition, QuickStart = QuickStart, SavedGame = SavedGame, UserMap = UserMap, MapPreview = (byte[])MapPreview.Clone() };
        foreach (var seat in seats) snapshot.Seats.Add(new SeatInfo
        {
            SeatId = seat.SeatId, LobbySlotIndex = seat.LobbySlotIndex, PlayerIndex = seat.PlayerIndex, DisplayName = seat.DisplayName,
            OriginallyHuman = seat.OriginallyHuman, Connected = seat.Connected, Ready = seat.Ready, AiControlled = seat.AiControlled, ClientId = seat.ClientId,
            Controller = seat.Controller, Team = seat.Team, Color = seat.Color, Position = seat.Position, PositionRandom = seat.PositionRandom,
            ResourceMultiplier = seat.ResourceMultiplier, AiIntelligence = seat.AiIntelligence, CommanderId = seat.CommanderId
            , CommanderMode = seat.CommanderMode, SkillId = seat.SkillId, Defeated = seat.Defeated,
            PendingActivation = seat.PendingActivation
        });
        for (var index = 0; index < seats.Count; index++)
            foreach (var passive in seats[index].PassiveIds) snapshot.Seats[index].PassiveIds.Add(passive);
        return snapshot;
    }

    public bool FinalizeDisconnected(Guid clientId)
    {
        var seat = seats.FirstOrDefault(s => s.ClientId == clientId && s.Connected);
        if (seat is null) return false;
        Release(seat); return true;
    }

    public bool ActivatePendingClaim(int playerIndex)
    {
        var seat = seats.FirstOrDefault(s => s.PlayerIndex == playerIndex && s.Connected && s.AiControlled && s.PendingActivation && !s.Defeated);
        if (seat is null) return false;
        seat.AiControlled = false; seat.PendingActivation = false; return true;
    }

    public int AvailableSeatCount => seats.Count(s => !s.Defeated && !s.Connected && (SavedGame || MatchStarted || s.OriginallyHuman));

    public void SetDefeated(int playerIndex, bool defeated)
    {
        var seat = seats.FirstOrDefault(s => s.PlayerIndex == playerIndex);
        if (seat is not null) seat.Defeated = defeated;
    }

    private static void Release(SeatInfo seat)
    {
        seat.ClientId = null; seat.DisplayName = seat.OriginallyHuman ? "空闲真人席位" : "原版 AI";
        seat.Connected = false; seat.Ready = false; seat.AiControlled = true; seat.PendingActivation = false;
    }
}
