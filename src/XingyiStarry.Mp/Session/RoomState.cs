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
    private string draftFingerprint = "";

    public RoomState(Guid? roomId = null) => RoomId = roomId ?? Guid.NewGuid();

    public void ReplaceSeats(IEnumerable<SeatInfo> values)
    {
        if (MatchStarted) throw new InvalidOperationException("Cannot replace seats after match start.");
        seats.Clear(); seats.AddRange(values); ClearReady();
    }

    public bool SyncDraft(string mapId, string mapTitle, int fowType, int winCondition, int quickStart, string fingerprint, IEnumerable<SeatInfo> values)
    {
        if (MatchStarted) return false;
        var desired = values.OrderBy(value => value.LobbySlotIndex).ToList();
        var changed = MapId != mapId || MapTitle != mapTitle || FowType != fowType || WinCondition != winCondition || QuickStart != quickStart || draftFingerprint != fingerprint ||
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
        MapId = mapId; MapTitle = mapTitle; FowType = fowType; WinCondition = winCondition; QuickStart = quickStart; draftFingerprint = fingerprint;
        DraftRevision++;
        return true;
    }

    public bool TryClaimHumanSeat(Guid clientId, string displayName, int lobbySlotIndex, out SeatInfo? claimed, out string reason)
    {
        claimed = seats.FirstOrDefault(s => s.LobbySlotIndex == lobbySlotIndex && s.OriginallyHuman);
        if (claimed is null) { reason = "目标不是可用的真人席位。"; return false; }
        if (claimed.Connected && claimed.ClientId != clientId) { reason = "该席位已被其他玩家占用。"; return false; }
        var previous = seats.FirstOrDefault(s => s.ClientId == clientId && s.Connected);
        if (previous is not null && previous != claimed)
        {
            previous.ClientId = null; previous.DisplayName = "空闲真人席位"; previous.Connected = false; previous.Ready = false; previous.AiControlled = true;
        }
        claimed.ClientId = clientId; claimed.DisplayName = displayName; claimed.Connected = true;
        if (previous != claimed) claimed.Ready = false;
        if (!MatchStarted) claimed.AiControlled = false;
        reason = ""; return true;
    }

    public void SetReady(Guid clientId, bool ready)
    {
        var seat = seats.Single(s => s.ClientId == clientId && s.Connected);
        seat.Ready = ready;
    }

    public bool CanStart => !string.IsNullOrEmpty(MapId) && seats.Any(s => s.OriginallyHuman) && seats.Where(s => s.OriginallyHuman).All(s => s.Connected && s.Ready);
    public void Start() { if (!CanStart) throw new InvalidOperationException("Not all connected human seats are ready."); MatchStarted = true; }
    public void ClearReady() { foreach (var seat in seats) seat.Ready = false; }

    public void BindRuntimePlayerIndices(IReadOnlyList<SGS_Player> players)
    {
        if (MatchStarted) throw new InvalidOperationException("Cannot bind player indices after match start.");
        foreach (var seat in seats)
        {
            if (seat.LobbySlotIndex < 0 || seat.LobbySlotIndex >= players.Count) throw new InvalidOperationException("Lobby slot is outside the start settings.");
            seat.PlayerIndex = players[seat.LobbySlotIndex].pos_ind;
            if (seat.PlayerIndex < 0) throw new InvalidOperationException("The game has not resolved random spawn positions yet.");
        }
        if (seats.Select(value => value.PlayerIndex).Distinct().Count() != seats.Count) throw new InvalidOperationException("Resolved player indices are not unique.");
    }

    public RoomSnapshot Snapshot()
    {
        var snapshot = new RoomSnapshot { RoomId = RoomId, MatchStarted = MatchStarted, DraftRevision = DraftRevision, MapId = MapId, MapTitle = MapTitle, FowType = FowType, WinCondition = WinCondition, QuickStart = QuickStart };
        foreach (var seat in seats) snapshot.Seats.Add(new SeatInfo
        {
            SeatId = seat.SeatId, LobbySlotIndex = seat.LobbySlotIndex, PlayerIndex = seat.PlayerIndex, DisplayName = seat.DisplayName,
            OriginallyHuman = seat.OriginallyHuman, Connected = seat.Connected, Ready = seat.Ready, AiControlled = seat.AiControlled, ClientId = seat.ClientId,
            Controller = seat.Controller, Team = seat.Team, Color = seat.Color, Position = seat.Position, PositionRandom = seat.PositionRandom,
            ResourceMultiplier = seat.ResourceMultiplier, AiIntelligence = seat.AiIntelligence, CommanderId = seat.CommanderId
            , CommanderMode = seat.CommanderMode, SkillId = seat.SkillId
        });
        for (var index = 0; index < seats.Count; index++)
            foreach (var passive in seats[index].PassiveIds) snapshot.Seats[index].PassiveIds.Add(passive);
        return snapshot;
    }

    public bool MarkDisconnected(Guid clientId)
    {
        var seat = seats.FirstOrDefault(s => s.ClientId == clientId && s.Connected);
        if (seat is null) return false;
        seat.Connected = false; seat.Ready = false; seat.ClientId = null; seat.AiControlled = true; return true;
    }

    public bool ActivatePendingClaim(int playerIndex)
    {
        var seat = seats.FirstOrDefault(s => s.PlayerIndex == playerIndex && s.OriginallyHuman && s.Connected && s.AiControlled);
        if (seat is null) return false;
        seat.AiControlled = false; return true;
    }
}
