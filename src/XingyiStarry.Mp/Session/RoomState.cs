using System;
using System.Collections.Generic;
using System.Linq;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal sealed class RoomState
{
    private readonly List<SeatInfo> seats = new List<SeatInfo>();
    private readonly List<ParticipantInfo> participants = new List<ParticipantInfo>();
    public Guid RoomId { get; }
    public IReadOnlyList<SeatInfo> Seats => seats;
    public IReadOnlyList<ParticipantInfo> Participants => participants;
    public int MaxParticipants { get; }
    public uint ParticipantRevision { get; private set; }
    public bool MatchStarted { get; private set; }
    public int DraftRevision { get; private set; }
    public string MapId { get; private set; } = "";
    public string MapTitle { get; private set; } = "";
    public int FowType { get; private set; }
    public int WinCondition { get; private set; }
    public int QuickStart { get; private set; }
    public int Difficulty { get; private set; } = 30;
    public bool SavedGame { get; private set; }
    public bool UserMap { get; private set; }
    public byte[] MapPreview { get; private set; } = Array.Empty<byte>();
    private string draftFingerprint = "";
    private readonly Dictionary<Guid, bool> initialDefeated = new Dictionary<Guid, bool>();

    public RoomState(Guid? roomId = null, int maxParticipants = 4)
    {
        if (maxParticipants < 1 || maxParticipants > 8) throw new ArgumentOutOfRangeException(nameof(maxParticipants));
        RoomId = roomId ?? Guid.NewGuid(); MaxParticipants = maxParticipants;
    }

    public void AddHost(Guid clientId, string displayName)
    {
        if (participants.Count != 0) throw new InvalidOperationException("Host must be the first participant.");
        participants.Add(new ParticipantInfo { ClientId = clientId, DisplayName = displayName, Connected = true,
            IsHost = true, Admission = ParticipantAdmission.Lobby });
        ParticipantRevision++;
    }

    public bool TryAddParticipant(Guid clientId, string displayName, out string reason)
    {
        if (participants.Count >= MaxParticipants) { reason = "${notice_the_room_is_full}"; return false; }
        if (participants.Any(value => value.ClientId == clientId)) { reason = "Duplicate participant identity."; return false; }
        participants.Add(new ParticipantInfo { ClientId = clientId, DisplayName = displayName, Connected = true,
            Admission = MatchStarted ? ParticipantAdmission.JoinSelection : ParticipantAdmission.Lobby });
        ParticipantRevision++; reason = ""; return true;
    }

    public ParticipantInfo? FindParticipant(Guid clientId) => participants.FirstOrDefault(value => value.ClientId == clientId);

    public void MarkReconnecting(Guid clientId, bool reconnecting)
    {
        var participant = FindParticipant(clientId); if (participant is null) return;
        participant.Reconnecting = reconnecting; participant.Connected = !reconnecting; ParticipantRevision++;
    }

    public void RemoveParticipant(Guid clientId)
    {
        var participant = FindParticipant(clientId); if (participant is null || participant.IsHost) return;
        participants.Remove(participant); ParticipantRevision++;
    }

    public void ReplaceSeats(IEnumerable<SeatInfo> values)
    {
        if (MatchStarted) throw new InvalidOperationException("Cannot replace seats after match start.");
        seats.Clear(); seats.AddRange(values); ClearReady(); RefreshParticipantSeats();
    }

    public void ConfigureSavedGame(string mapId, string mapTitle, int fowType, int winCondition, int quickStart, int difficulty, IEnumerable<SeatInfo> values)
    {
        if (MatchStarted) throw new InvalidOperationException("Cannot configure a saved game after match start.");
        SavedGame = true; UserMap = false; MapPreview = Array.Empty<byte>(); MapId = mapId; MapTitle = mapTitle; FowType = fowType; WinCondition = winCondition; QuickStart = quickStart; Difficulty = difficulty;
        seats.Clear(); seats.AddRange(values); ClearReady(); RefreshParticipantSeats(); DraftRevision++;
    }

    public bool SyncDraft(string mapId, string mapTitle, int fowType, int winCondition, int quickStart, int difficulty, bool userMap, byte[] mapPreview, string fingerprint, IEnumerable<SeatInfo> values)
    {
        if (MatchStarted) return false;
        var desired = values.OrderBy(value => value.LobbySlotIndex).ToList();
        var changed = MapId != mapId || MapTitle != mapTitle || FowType != fowType || WinCondition != winCondition || QuickStart != quickStart || Difficulty != difficulty ||
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
        MapId = mapId; MapTitle = mapTitle; FowType = fowType; WinCondition = winCondition; QuickStart = quickStart; Difficulty = difficulty;
        UserMap = userMap; MapPreview = (byte[])mapPreview.Clone(); draftFingerprint = fingerprint;
        RefreshParticipantSeats(); ClearReady(); DraftRevision++;
        return true;
    }

    public bool TryClaimSeat(Guid clientId, string displayName, int lobbySlotIndex, out SeatInfo? claimed, out string reason)
    {
        var participant = FindParticipant(clientId);
        if (participant is null) { claimed = null; reason = "Participant does not exist."; return false; }
        if (participant.Admission == ParticipantAdmission.Spectator || participant.Admission == ParticipantAdmission.Player)
        { claimed = null; reason = "Seats cannot be changed after entering the match."; return false; }
        claimed = seats.FirstOrDefault(s => s.LobbySlotIndex == lobbySlotIndex && (SavedGame || MatchStarted || s.OriginallyHuman));
        if (claimed is null) { reason = "The selected seat is not available."; return false; }
        if (claimed.Defeated) { reason = "Defeated seats cannot be taken over."; return false; }
        if (claimed.Connected && claimed.ClientId != clientId) { reason = "This seat is occupied by another player."; return false; }
        var previous = seats.FirstOrDefault(s => s.ClientId == clientId && s.Connected);
        if (previous is not null && previous != claimed)
        {
            Release(previous);
        }
        claimed.ClientId = clientId; claimed.DisplayName = displayName; claimed.Connected = true;
        if (previous != claimed) { claimed.Ready = false; participant.Ready = false; }
        claimed.PendingActivation = MatchStarted;
        claimed.AiControlled = MatchStarted;
        participant.SeatId = claimed.SeatId; ParticipantRevision++;
        reason = ""; return true;
    }

    public bool ReleaseSeat(Guid clientId)
    {
        var seat = seats.FirstOrDefault(s => s.ClientId == clientId && s.Connected);
        if (seat is null) return false;
        Release(seat);
        var participant = FindParticipant(clientId);
        if (participant is not null) { participant.SeatId = null; participant.Ready = false; ParticipantRevision++; }
        return true;
    }

    public void SetReady(Guid clientId, bool ready)
    {
        if (MatchStarted) throw new InvalidOperationException("Cannot ready after match start.");
        var participant = participants.Single(value => value.ClientId == clientId && value.Connected);
        participant.Ready = ready;
        var seat = seats.FirstOrDefault(value => value.ClientId == clientId && value.Connected);
        if (seat is not null) seat.Ready = ready;
        ParticipantRevision++;
    }

    public bool CanStart => !MatchStarted && participants.Count != 0 && participants.Where(value => value.Connected).All(value => value.Ready) && (SavedGame
        ? !string.IsNullOrEmpty(MapId) && seats.Any(s => s.Connected && !s.Defeated)
        : !string.IsNullOrEmpty(MapId) && seats.Any(s => s.OriginallyHuman) && seats.Where(s => s.OriginallyHuman).All(s => s.Connected));
    public void Start()
    {
        if (!CanStart) throw new InvalidOperationException("Not all participants and human seats are ready.");
        MatchStarted = true;
        initialDefeated.Clear();
        foreach (var seat in seats) initialDefeated[seat.SeatId] = seat.Defeated;
        foreach (var participant in participants)
            participant.Admission = participant.SeatId.HasValue ? ParticipantAdmission.Player : ParticipantAdmission.Spectator;
        ParticipantRevision++;
    }
    public void ReturnToLobby()
    {
        if (!MatchStarted) return;
        MatchStarted = false;
        foreach (var participant in participants)
            participant.Admission = ParticipantAdmission.Lobby;
        foreach (var seat in seats)
        {
            seat.Defeated = initialDefeated.TryGetValue(seat.SeatId, out var defeated) && defeated;
            seat.PendingActivation = false;
            seat.AiControlled = !seat.Connected;
            // A human who took an AI seat mid-match keeps it for the next game.
            if (!SavedGame && seat.Connected) { seat.OriginallyHuman = true; seat.Controller = 0; }
        }
        ClearReady();
        DraftRevision++;
    }
    public void ClearReady()
    {
        foreach (var participant in participants) participant.Ready = false;
        foreach (var seat in seats) seat.Ready = false;
        ParticipantRevision++;
    }

    public bool AdmitToMatch(Guid clientId, Guid? seatId, out bool spectator, out string reason)
    {
        spectator = false;
        if (!MatchStarted) { reason = "Match has not started."; return false; }
        var participant = FindParticipant(clientId);
        if (participant is null || participant.Admission != ParticipantAdmission.JoinSelection)
        { reason = "This connection is not selecting a seat for an ongoing match."; return false; }
        if (seatId.HasValue)
        {
            var seat = seats.FirstOrDefault(value => value.SeatId == seatId.Value && value.ClientId == clientId && value.Connected && value.PendingActivation);
            if (seat is null) { reason = "Select an available seat first."; return false; }
            participant.SeatId = seat.SeatId; participant.Admission = ParticipantAdmission.Player;
        }
        else
        {
            if (participant.SeatId.HasValue) ReleaseSeat(clientId);
            participant.Admission = ParticipantAdmission.Spectator; spectator = true;
        }
        ParticipantRevision++; reason = ""; return true;
    }

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
        var snapshot = new RoomSnapshot { RoomId = RoomId, MatchStarted = MatchStarted, DraftRevision = DraftRevision, MapId = MapId, MapTitle = MapTitle, FowType = FowType, WinCondition = WinCondition, QuickStart = QuickStart, Difficulty = Difficulty, SavedGame = SavedGame, UserMap = UserMap, MapPreview = (byte[])MapPreview.Clone(), MaxParticipants = MaxParticipants, ParticipantRevision = ParticipantRevision };
        foreach (var seat in seats) snapshot.Seats.Add(new SeatInfo
        {
            SeatId = seat.SeatId, LobbySlotIndex = seat.LobbySlotIndex, PlayerIndex = seat.PlayerIndex, DisplayName = seat.DisplayName,
            OriginallyHuman = seat.OriginallyHuman, Connected = seat.Connected, Ready = seat.Ready, AiControlled = seat.AiControlled, ClientId = seat.ClientId,
            Controller = seat.Controller, Team = seat.Team, Color = seat.Color, Position = seat.Position, PositionRandom = seat.PositionRandom,
            ResourceMultiplier = seat.ResourceMultiplier, AiIntelligence = seat.AiIntelligence, CommanderId = seat.CommanderId
            , CommanderMode = seat.CommanderMode, KeepMapCommander = seat.KeepMapCommander, SkillId = seat.SkillId, Defeated = seat.Defeated,
            PendingActivation = seat.PendingActivation
        });
        for (var index = 0; index < seats.Count; index++)
            foreach (var passive in seats[index].PassiveIds) snapshot.Seats[index].PassiveIds.Add(passive);
        foreach (var participant in participants) snapshot.Participants.Add(new ParticipantInfo
        {
            ClientId = participant.ClientId, DisplayName = participant.DisplayName, Ready = participant.Ready,
            Connected = participant.Connected, Reconnecting = participant.Reconnecting, IsHost = participant.IsHost,
            Admission = participant.Admission, SeatId = participant.SeatId
        });
        return snapshot;
    }

    public bool FinalizeDisconnected(Guid clientId)
    {
        var seat = seats.FirstOrDefault(s => s.ClientId == clientId && s.Connected);
        if (seat is null) return false;
        Release(seat);
        var participant = FindParticipant(clientId);
        if (participant is not null) { participant.SeatId = null; participant.Ready = false; ParticipantRevision++; }
        return true;
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
        seat.ClientId = null; seat.DisplayName = seat.OriginallyHuman ? "${ui_unclaimed_human_seat}" : "${ui_ai}";
        seat.Connected = false; seat.Ready = false; seat.AiControlled = true; seat.PendingActivation = false;
    }

    private void RefreshParticipantSeats()
    {
        foreach (var participant in participants)
        {
            var seat = seats.FirstOrDefault(value => value.ClientId == participant.ClientId && value.Connected);
            participant.SeatId = seat?.SeatId;
            if (seat is null) participant.Ready = false;
        }
    }
}
