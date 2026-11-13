using System;
using System.IO;
using System.Threading;
using Google.Protobuf.Collections;
using XingyiStarry.Mp.Protocol.Wire;

namespace XingyiStarry.Mp.Protocol;

// A synchronous native call owns its results. Nested calls get independent contexts.
public sealed class RandomResultContext : IDisposable
{
    private static readonly AsyncLocal<RandomResultContext?> current = new AsyncLocal<RandomResultContext?>();
    public static RandomResultContext? Current => current.Value;
    private readonly RandomResultContext? previous;
    private readonly RandomResult result;
    private readonly bool replay;
    private readonly long unitId;
    private readonly int x, y;
    private int damageIndex, deathIndex, wreckIndex, commanderIndex, experienceIndex;
    private bool disposed;

    public RandomResultContext(RandomResult result, bool replay, long unitId, int x = 0, int y = 0)
    {
        this.result = result; this.replay = replay; this.unitId = unitId; this.x = x; this.y = y;
        previous = current.Value; current.Value = this;
    }

    public float Float(string site, Func<float> original) => (float)Take(site, false, () => original()).Floating;
    public int Integer(string site, Func<int> original) => checked((int)Take(site, true, () => original()).Integer);

    private RandomRecordMessage Take(string site, bool integer, Func<double> original)
    {
        RandomRecordMessage? record;
        int ordinal;
        if (site == "UnitData.Hurt")
        {
            ordinal = damageIndex++;
            if (ordinal > 1) throw new InvalidDataException("Unexpected damage random result.");
            record = ordinal == 0 ? result.DamageRoll : result.DamageVariation;
            if (!replay) { record = New(site, 0, integer, original()); if (ordinal == 0) result.DamageRoll = record; else result.DamageVariation = record; }
        }
        else if (site == "UnitData.Die")
        {
            ordinal = deathIndex++;
            if (ordinal != 0) throw new InvalidDataException("Unexpected death random result.");
            record = result.DeathWreckScale;
            if (!replay) result.DeathWreckScale = record = New(site, ordinal, integer, original());
        }
        else if (site == "GameTileData.CreateWreck")
        {
            ordinal = wreckIndex++;
            if (!replay) result.Wrecks.Add(new TileRandomResult { X = x, Y = y, Values = new RandomRecordList() });
            if (ordinal >= result.Wrecks.Count) throw new InvalidDataException("Missing wreck result.");
            var tile = result.Wrecks[ordinal];
            if (tile.X != x || tile.Y != y) throw new InvalidDataException("Wreck result belongs to another tile.");
            if (tile.Values is null || replay && tile.Values.Records.Count != 1) throw new InvalidDataException("Invalid wreck result group.");
            record = ListRecord(tile.Values.Records, site, 0, integer, original);
        }
        else if (site == "Player.AutoSetCmdPos")
        {
            ordinal = commanderIndex++;
            record = UnitRecord(result.CommanderPositions, site, ordinal, integer, original);
        }
        else if (site == "UnitEffect_ShareExp.PreAddExp/Shuffle")
        {
            ordinal = experienceIndex++;
            record = UnitRecord(result.ExperienceShares, site, ordinal, integer, original);
        }
        else throw new InvalidDataException("Unknown gameplay random purpose: " + site);
        if (record is null || record.CallSite != site || record.Ordinal != (site == "UnitData.Hurt" || site == "GameTileData.CreateWreck" ? 0 : ordinal) ||
            record.ValueCase != (integer ? RandomRecordMessage.ValueOneofCase.Integer : RandomRecordMessage.ValueOneofCase.Floating))
            throw new InvalidDataException("Random result mismatch for " + site + " ordinal " + ordinal);
        return record;
    }

    private RandomRecordMessage UnitRecord(RepeatedField<UnitRandomResult> groups, string site, int ordinal, bool integer, Func<double> original)
    {
        if (!replay && groups.Count == 0) groups.Add(new UnitRandomResult { UnitId = unitId, Values = new RandomRecordList() });
        if (groups.Count != 1 || groups[0].UnitId != unitId || groups[0].Values is null) throw new InvalidDataException("Random result unit mismatch.");
        return ListRecord(groups[0].Values.Records, site, ordinal, integer, original);
    }

    private RandomRecordMessage ListRecord(RepeatedField<RandomRecordMessage> values, string site, int ordinal, bool integer, Func<double> original)
    {
        if (!replay) { var record = New(site, ordinal, integer, original()); values.Add(record); return record; }
        // A wreck has one record per tile group; other lists use a local purpose ordinal.
        var index = site == "GameTileData.CreateWreck" ? 0 : ordinal;
        if (index >= values.Count) throw new InvalidDataException("Missing random result for " + site);
        return values[index];
    }

    private static RandomRecordMessage New(string site, int ordinal, bool integer, double value)
    {
        var record = new RandomRecordMessage { CallSite = site, Ordinal = ordinal };
        if (integer) record.Integer = checked((long)value); else record.Floating = value;
        return record;
    }

    public void Complete()
    {
        if (!replay) return;
        foreach (var tile in result.Wrecks)
            if (tile.Values is null || tile.Values.Records.Count != 1) throw new InvalidDataException("Settlement contains unused wreck results.");
        if (damageIndex != (result.DamageRoll is null ? 0 : 1) + (result.DamageVariation is null ? 0 : 1) ||
            deathIndex != (result.DeathWreckScale is null ? 0 : 1) || wreckIndex != result.Wrecks.Count ||
            commanderIndex != Count(result.CommanderPositions) || experienceIndex != Count(result.ExperienceShares))
            throw new InvalidDataException("Settlement contains unused random results.");
    }
    private static int Count(RepeatedField<UnitRandomResult> values)
    {
        if (values.Count == 0) return 0;
        if (values.Count != 1 || values[0].Values is null || values[0].Values.Records.Count == 0)
            throw new InvalidDataException("Invalid unit random result group.");
        return values[0].Values.Records.Count;
    }
    public void Dispose() { if (disposed) return; disposed = true; current.Value = previous; }
}
