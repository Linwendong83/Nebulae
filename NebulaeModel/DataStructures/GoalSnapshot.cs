using System.Collections.Generic;
using NebulaeModel.Networking.Serialization;

namespace NebulaeModel.DataStructures;

internal sealed class GoalSnapshot
{
    public bool SandboxMode { get; private set; }
    public bool SandboxToolsEnabled { get; private set; }
    public EGoalLevel Level { get; private set; }
    public GoalSnapshotEntry[] Goals { get; private set; }
    public int[] Queue { get; private set; }
    public EGoalLevel DefaultLevel { get; private set; }
    public int[] IgnoredGoals { get; private set; }

    public static GoalSnapshot Read(byte[] bytes, int maxGoals, int maxQueue)
    {
        if (bytes == null) throw new ParseException("Missing goal snapshot");
        var reader = new NetDataReader(bytes);
        var snapshot = new GoalSnapshot
        {
            SandboxMode = reader.GetBool(),
            SandboxToolsEnabled = reader.GetBool(),
            Level = ReadLevel(reader)
        };
        var count = ReadCount(reader, maxGoals, 30);
        snapshot.Goals = new GoalSnapshotEntry[count];
        var ids = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var id = reader.GetInt();
            var stage = (EGoalStage)reader.GetInt();
            if (id <= 0 || !ids.Add(id) || stage < EGoalStage.Disabled || stage > EGoalStage.Completed)
                throw new ParseException("Invalid goal identity or stage");
            snapshot.Goals[i] = new GoalSnapshotEntry(id, stage, reader.GetLong(), reader.GetLong(),
                reader.GetBool(), reader.GetBool(), reader.GetInt());
        }
        snapshot.Queue = ReadIds(reader, maxQueue);
        snapshot.DefaultLevel = ReadLevel(reader);
        snapshot.IgnoredGoals = ReadIds(reader, maxGoals);
        if (!reader.EndOfData) throw new ParseException("Trailing goal snapshot data");
        return snapshot;
    }

    private static EGoalLevel ReadLevel(NetDataReader reader)
    {
        var level = (EGoalLevel)reader.GetInt();
        if (level < EGoalLevel.Off || level > EGoalLevel.Full) throw new ParseException("Invalid goal level");
        return level;
    }

    private static int ReadCount(NetDataReader reader, int maximum, int entryBytes)
    {
        var count = reader.GetInt();
        if (count < 0 || count > maximum || count > reader.AvailableBytes / entryBytes)
            throw new ParseException("Invalid goal snapshot count");
        return count;
    }

    private static int[] ReadIds(NetDataReader reader, int maximum)
    {
        var count = ReadCount(reader, maximum, sizeof(int));
        var values = new int[count];
        var ids = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var id = reader.GetInt();
            if (id <= 0 || !ids.Add(id)) throw new ParseException("Invalid goal reference");
            values[i] = id;
        }
        return values;
    }
}

internal readonly struct GoalSnapshotEntry(int id, EGoalStage stage, long current, long target,
    bool ignored, bool patched, int ignoreLevel)
{
    public int Id { get; } = id;
    public EGoalStage Stage { get; } = stage;
    public long Current { get; } = current;
    public long Target { get; } = target;
    public bool Ignored { get; } = ignored;
    public bool Patched { get; } = patched;
    public int IgnoreLevel { get; } = ignoreLevel;
}
