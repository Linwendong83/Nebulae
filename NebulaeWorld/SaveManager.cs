#region

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NebulaeModel.Contracts;
using NebulaeModel.Authority;
using NebulaeModel.DataStructures;
using NebulaeModel.Logger;
using NebulaeModel.Networking.Serialization;
using NebulaeModel.Utils;

#endregion

namespace NebulaeWorld;

internal static class SaveManager
{
    private const string FILE_EXTENSION = ".server";
    private const ushort REVISION = 13;
    public static string WorldId { get; private set; } = Guid.NewGuid().ToString("N");

    public static void SetWorldId(string worldId)
    {
        if (!Guid.TryParseExact(worldId, "N", out _)) throw new InvalidDataException("Invalid multiplayer world ID");
        WorldId = worldId;
    }

    public static void BindWorldIdentity(GameData data)
    {
        const int first = -1708469020;
        const int tag = -1708469024;
        const int magic = 0x4e42574c;
        var values = data.history.featureValues;
        if (values.TryGetValue(tag, out var marker) && marker == magic)
        {
            var bytes = new byte[16];
            for (var i = 0; i < 4; i++)
            {
                if (!values.TryGetValue(first - i, out var part)) throw new InvalidDataException("Incomplete multiplayer world identity");
                Array.Copy(BitConverter.GetBytes(part), 0, bytes, i * 4, 4);
            }
            var worldId = new Guid(bytes).ToString("N");
            if (loadedServerIdentity && !string.Equals(WorldId, worldId, StringComparison.Ordinal))
            {
                serverDataLoadFailed = true;
                throw new InvalidDataException("The world and multiplayer save have different identities");
            }
            WorldId = worldId;
            return;
        }
        var identity = Guid.ParseExact(WorldId, "N").ToByteArray();
        for (var i = 0; i < 4; i++) values[first - i] = BitConverter.ToInt32(identity, i * 4);
        values[tag] = magic;
    }

    private static readonly Dictionary<string, IPlayerData> playerSaves = new();
    private static string pendingLoadFileName;
    private static bool pendingLoadSaveFile;
    private static bool hasPendingLoad;
    private static bool serverDataLoadFailed;
    private static byte[] legacyAuthorityArchive;
    private static bool loadedServerIdentity;

    public static bool CanSave => !serverDataLoadFailed;
    public static IReadOnlyDictionary<string, IPlayerData> PlayerSaves => playerSaves;

    public static void SaveServerData(string saveName)
    {
        if (!CanSave)
            throw new InvalidOperationException("Multiplayer data could not be loaded; refusing to overwrite the save.");
        Multiplayer.Session.Kills.CapturePlayersForSave();
        Multiplayer.Session.Vegetation.CaptureRemoteForSave();
        var path = GameConfig.gameSaveFolder + saveName + FILE_EXTENSION;
        var netDataWriter = new NetDataWriter();
        netDataWriter.Put("REV");
        netDataWriter.Put(REVISION);
        netDataWriter.Put(WorldId);

        var includeHost = !Multiplayer.Session.IsDedicated;
        var hostIdentity = includeHost ? CryptoUtils.GetCurrentUserPublicKeyHash() : null;
        var entries = GetPlayerSaveEntries(hostIdentity);
        netDataWriter.Put(entries.Length + (includeHost ? 1 : 0));
        //Add data about all players
        foreach (var data in entries)
        {
            var hash = data.Key;
            netDataWriter.Put(hash);
            data.Value.Serialize(netDataWriter);
        }

        Log.Info(
            $"Saving server data to {saveName + FILE_EXTENSION}, Revision:{REVISION} PlayerCount:{playerSaves.Count}");

        //Add host's data
        if (includeHost)
        {
            netDataWriter.Put(hostIdentity);
            // Scalars and inventory must describe this save, not the host's last life event.
            NebulaeWorld.GameStates.MetadataTransactionManager.CapturePlayer();
            var hostData = (PlayerData)Multiplayer.Session.LocalPlayer.Data;
            hostData.VegetableCollectionData = VegetableCollectionState.Capture(GameMain.mainPlayer.vegetableCollection);
            hostData.Serialize(netDataWriter);
        }

        // Encode and validate every section before publishing one complete server file.
        ServerSaveAuthorityArchive.Append(netDataWriter, legacyAuthorityArchive, WorldId);
        AtomicFile.Write(path, netDataWriter.CopyData());

        // If the saveName is the autoSave, we need to rotate the server autosave file.
        if (saveName == GameSave.AutoSaveTmp)
        {
            HandleAutoSave();
        }
    }

    private static void HandleAutoSave()
    {
        ServerSaveRotation.Rotate(GameConfig.gameSaveFolder, GameSave.AutoSaveTmp,
            new[] { GameSave.AutoSave0, GameSave.AutoSave1, GameSave.AutoSave2, GameSave.AutoSave3 },
            new[] { FILE_EXTENSION });
    }

    private static KeyValuePair<string, IPlayerData>[] GetPlayerSaveEntries(string hostIdentity) =>
        playerSaves.Where(entry => entry.Key != hostIdentity).ToArray();

    public static void LoadServerData(bool loadSaveFile)
    {
        // A dedicated server opens its session before the world is created, so GameMain.data is still
        // null here and player data cannot be deserialized (CombatModuleComponent.Init needs GameData).
        // Remember the request and finish it in EnsureServerDataLoaded() once the world exists.
        // GameData.Import() resets DSPGame.LoadFile to "" while reading the save, so capture the name
        // now instead of relying on it later.
        if (loadSaveFile && GameMain.data == null)
        {
            pendingLoadFileName = DSPGame.LoadFile;
            pendingLoadSaveFile = true;
            hasPendingLoad = true;
            return;
        }

        LoadServerDataNow(loadSaveFile, DSPGame.LoadFile);
    }

    // Completes a LoadServerData() call that had to wait for the world to be created.
    public static void EnsureServerDataLoaded()
    {
        if (!hasPendingLoad || GameMain.data == null)
        {
            return;
        }

        hasPendingLoad = false;
        LoadServerDataNow(pendingLoadSaveFile, pendingLoadFileName);
    }

    private static void LoadServerDataNow(bool loadSaveFile, string saveName)
    {
        serverDataLoadFailed = false;
        legacyAuthorityArchive = null;
        loadedServerIdentity = false;
        playerSaves.Clear();
        WorldId = Guid.NewGuid().ToString("N");

        if (!loadSaveFile)
        {
            return;
        }
        var path = GameConfig.gameSaveFolder + saveName + FILE_EXTENSION;
        if (!File.Exists(path))
        {
            TryReadLegacyIdentity(path);
            Log.Info("No server file");
            return;
        }

        try
        {
            var source = File.ReadAllBytes(path);
            var netDataReader = new NetDataReader(source);
            ushort revision;

            var revString = netDataReader.GetString();
            if (revString != "REV")
            {
                throw new Exception("Incorrect header");
            }

            revision = netDataReader.GetUShort();
            Log.Info($"Loading server data revision {revision} (Latest {REVISION})");
            if (revision != REVISION)
            {
                // Supported revision: 5~12. Revision 10 adds each player's vegetation collection,
                // revision 11 adds the persisted flight state (movement state + velocities),
                // revision 12 adds each seat's local star.
                if (revision is < 5 or > REVISION)
                {
                    throw new Exception($"Unsupported version {revision}");
                }
            }

            if (revision >= 9)
            {
                SetWorldId(netDataReader.GetString());
                loadedServerIdentity = true;
            }
            else TryReadLegacyIdentity(path);

            var playerNum = netDataReader.GetInt();
            if (playerNum < 0 || playerNum > ushort.MaxValue) throw new InvalidDataException("Invalid player count");


            for (var i = 0; i < playerNum; i++)
            {
                var hash = netDataReader.GetString();
                var playerData = new PlayerData();
                playerData.Import(netDataReader, revision);

                if (!playerSaves.ContainsKey(hash))
                {
                    playerData.PersistentId = hash;
                    playerSaves.Add(hash, playerData);
                }
            }
            legacyAuthorityArchive = ServerSaveAuthorityArchive.Read(netDataReader, WorldId);
        }
        catch (Exception e)
        {
            serverDataLoadFailed = true;
            playerSaves.Clear();
            Log.Warn("Multiplayer data load failed; saving is disabled to preserve the original file:\n".Translate() + e.Message);
            Log.Warn(e);
            return;
        }

        if (legacyAuthorityArchive == null) ReadLegacyAuthorityArchive(path);
    }

    private static void TryReadLegacyIdentity(string serverPath)
    {
        // Only old saves without an embedded identity need this migration fallback.
        // Never create or update the retired companion file.
        var identityPath = serverPath + ".world-id";
        if (!File.Exists(identityPath)) return;
        try { SetWorldId(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(identityPath))); }
        catch (Exception error) { Log.Warn("Ignoring obsolete world identity file: " + error.Message); }
    }

    private static void ReadLegacyAuthorityArchive(string serverPath)
    {
        foreach (var path in ServerSaveAuthorityArchive.LegacyPaths(serverPath))
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > AuthoritySidecarLimits.FileMaxBytes)
                    throw new InvalidDataException("Legacy authority file exceeds its size ceiling");
                var bytes = File.ReadAllBytes(path);
                ServerSaveAuthorityArchive.Validate(bytes, WorldId);
                legacyAuthorityArchive = bytes;
                Log.Info("Historical authority facts will be preserved inside the next .server save: " + path);
                return;
            }
            catch (Exception error)
            {
                // These retired simulation records must not override native player/world facts
                // or disable saving. Leave the original file untouched for recovery.
                Log.Warn("Legacy authority archive was not imported: " + path + ": " + error.Message);
            }
        }
    }

    public static bool TryAdd(string clientCertHash, IPlayerData playerData)
    {
        ((PlayerData)playerData).PersistentId = clientCertHash;
        if (playerSaves.ContainsKey(clientCertHash))
        {
            return false;
        }

        playerSaves.Add(clientCertHash, playerData);
        return true;
    }

    public static bool TryRemove(string clientCertHash)
    {
        return playerSaves.Remove(clientCertHash);
    }
}
