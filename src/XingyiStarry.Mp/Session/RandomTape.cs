using System;
using System.Collections.Generic;
using System.IO;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal sealed class RandomTape
{
    private readonly List<RandomRecord> hostRecords = new List<RandomRecord>();
    private Queue<RandomRecord>? replayRecords;
    private int ordinal;

    public bool IsRecording { get; private set; }
    public bool IsReplaying => replayRecords is not null;

    public void BeginRecording() { Reset(); IsRecording = true; }
    public void BeginReplay(IEnumerable<RandomRecord> records) { Reset(); replayRecords = new Queue<RandomRecord>(records); }

    public float Float(string callSite, Func<float> original)
    {
        if (IsRecording)
        {
            var value = original(); hostRecords.Add(new RandomRecord { CallSite = callSite, Ordinal = ordinal++, ValueKind = 1, FloatingValue = value }); return value;
        }
        var record = Consume(callSite, 1); return (float)record.FloatingValue;
    }

    public int Integer(string callSite, Func<int> original)
    {
        if (IsRecording)
        {
            var value = original(); hostRecords.Add(new RandomRecord { CallSite = callSite, Ordinal = ordinal++, ValueKind = 2, IntegerValue = value }); return value;
        }
        var record = Consume(callSite, 2); return checked((int)record.IntegerValue);
    }

    public IReadOnlyList<RandomRecord> EndRecording()
    {
        if (!IsRecording) throw new InvalidOperationException("Random tape is not recording.");
        IsRecording = false; return hostRecords.ToArray();
    }

    public void EndReplay()
    {
        if (replayRecords is null) throw new InvalidOperationException("Random tape is not replaying.");
        if (replayRecords.Count != 0) throw new InvalidDataException("Authority resolution contains unused random values.");
        replayRecords = null;
    }

    private RandomRecord Consume(string callSite, byte kind)
    {
        if (replayRecords is null || replayRecords.Count == 0) throw new InvalidDataException("Authority resolution is missing a random value.");
        var record = replayRecords.Dequeue();
        if (record.CallSite != callSite || record.Ordinal != ordinal++ || record.ValueKind != kind) throw new InvalidDataException("Random call-site sequence mismatch.");
        return record;
    }

    private void Reset() { hostRecords.Clear(); replayRecords = null; ordinal = 0; IsRecording = false; }
}
