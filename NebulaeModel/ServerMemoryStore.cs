using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BepInEx;
using NebulaeModel.Logger;
using NebulaeModel.Utils;

namespace NebulaeModel;

[DataContract]
internal sealed class SavedServer
{
    // Names are pinned: these documents live on disk under BepInEx/Nebulae and must stay
    // readable and writable across mod updates, whatever the members are called in code.
    [DataMember(Name = "Id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [DataMember(Name = "Name")] public string Name { get; set; } = "";
    [DataMember(Name = "Address")] public string Address { get; set; } = "";
    [DataMember(Name = "Password")] public string Password { get; set; } = "";
    /// <summary>0 = ask on first join; otherwise the EGoalLevel.Off..Full range picked for this server.</summary>
    [DataMember(Name = "GoalLevel")] public int GoalLevel { get; set; }
}

[DataContract]
internal sealed class PersonalGoalProfile
{
    [DataMember(Name = "Key")] public string Key { get; set; } = "";
    [DataMember(Name = "Level")] public int Level { get; set; }
    [DataMember(Name = "IgnoredGoalIds")] public List<int> IgnoredGoalIds { get; set; } = new();
}

/// <summary>Local server memories and player-specific goal preferences. No legacy IP import.</summary>
internal sealed class ServerMemoryStore
{
    [DataContract]
    private sealed class ServerDocument
    {
        [DataMember(Name = "Servers")] public List<SavedServer> Servers { get; set; } = new();
    }

    [DataContract]
    private sealed class GoalDocument
    {
        [DataMember(Name = "Profiles")] public List<PersonalGoalProfile> Profiles { get; set; } = new();
    }

    private readonly string serverPath;
    private readonly string goalPath;
    private readonly ServerDocument serverDocument;
    private readonly GoalDocument goalDocument;
    private static ServerMemoryStore instance;

    public static ServerMemoryStore Instance => instance ??= new ServerMemoryStore(
        Path.Combine(LegacyDataMigration.DataDirectory(Paths.ConfigPath), "servers.json"),
        Path.Combine(LegacyDataMigration.DataDirectory(Paths.ConfigPath), "goal-profiles.json"));

    public ServerMemoryStore(string serverPath, string goalPath)
    {
        this.serverPath = serverPath;
        this.goalPath = goalPath;
        serverDocument = Read<ServerDocument>(serverPath) ?? new ServerDocument();
        goalDocument = Read<GoalDocument>(goalPath) ?? new GoalDocument();
        serverDocument.Servers ??= new List<SavedServer>();
        goalDocument.Profiles ??= new List<PersonalGoalProfile>();
        serverDocument.Servers.RemoveAll(s => s == null || !Guid.TryParseExact(s.Id, "N", out _) || string.IsNullOrWhiteSpace(s.Address));
        goalDocument.Profiles.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Key));
    }

    public IReadOnlyList<SavedServer> Servers => serverDocument.Servers;

    public SavedServer FindServer(string id) => serverDocument.Servers.FirstOrDefault(s => s.Id == id);

    public SavedServer SaveServer(string id, string name, string address, string password)
    {
        name = name?.Trim() ?? "";
        address = address?.Trim() ?? "";
        if (name.Length == 0 || address.Length == 0) throw new ArgumentException("Name and address are required");
        var server = FindServer(id);
        if (server == null)
        {
            server = new SavedServer();
            serverDocument.Servers.Add(server);
        }
        server.Name = name;
        server.Address = address;
        server.Password = password ?? "";
        Write(serverPath, serverDocument);
        return server;
    }

    public bool UpdatePassword(string id, string password)
    {
        var server = FindServer(id);
        if (server == null) return false;
        server.Password = password ?? "";
        Write(serverPath, serverDocument);
        return true;
    }

    /// <summary>
    /// Store the goal level chosen for a server and keep every world profile of that
    /// server on the same level (their ignored goals are preserved).
    /// </summary>
    public void UpdateGoalLevel(string id, int level)
    {
        var server = FindServer(id);
        if (server == null) return;
        server.GoalLevel = level;
        Write(serverPath, serverDocument);
        if (level < 1 || level > 3) return; // Outside EGoalLevel.Off..Full; 0 means "ask on join".
        var prefix = id + ":";
        foreach (var profile in goalDocument.Profiles)
            if (profile.Key.StartsWith(prefix, StringComparison.Ordinal)) profile.Level = level;
        Write(goalPath, goalDocument);
    }

    public void DeleteServer(string id)
    {
        if (serverDocument.Servers.RemoveAll(s => s.Id == id) == 0) return;
        goalDocument.Profiles.RemoveAll(p => p.Key.StartsWith(id + ":", StringComparison.Ordinal));
        Write(serverPath, serverDocument);
        Write(goalPath, goalDocument);
    }

    public PersonalGoalProfile FindProfile(string recordId, string worldId, string identity)
    {
        var key = ProfileKey(recordId, worldId, identity);
        return goalDocument.Profiles.FirstOrDefault(p => p.Key == key);
    }

    public PersonalGoalProfile SaveProfile(string recordId, string worldId, string identity, int level,
        IEnumerable<int> ignored)
    {
        var key = ProfileKey(recordId, worldId, identity);
        var profile = goalDocument.Profiles.FirstOrDefault(p => p.Key == key);
        if (profile == null)
        {
            profile = new PersonalGoalProfile { Key = key };
            goalDocument.Profiles.Add(profile);
        }
        profile.Level = level;
        profile.IgnoredGoalIds = ignored.Distinct().OrderBy(x => x).ToList();
        Write(goalPath, goalDocument);
        return profile;
    }

    private static string ProfileKey(string recordId, string worldId, string identity) =>
        (string.IsNullOrEmpty(recordId) ? "host" : recordId) + ":" + worldId + ":" +
        AtomicFile.IdentityFileName(identity);

    private static T Read<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var stream = File.OpenRead(candidate);
                return new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
            }
            catch (Exception e) { Log.Warn($"Could not load {candidate}: {e.Message}"); }
        }
        return null;
    }

    private static void Write<T>(string path, T document)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, document);
        AtomicFile.Write(path, stream.ToArray());
    }
}
