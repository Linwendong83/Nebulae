#region

using System;
using NebulaeModel.DataStructures;
using NebulaeModel.DataStructures.Chat;
using NebulaeModel.Packets.Chat;
using NebulaeWorld.Chat.ChatLinks;
using NebulaeWorld.MonoBehaviours.Local.Chat;
using UnityEngine;
#pragma warning disable IDE1006 // Naming Styles
#pragma warning disable CA1822 // Mark members as static

#endregion

namespace NebulaeWorld.Combat;

internal partial class EnemyManager : IDisposable
{
    public readonly ToggleSwitch IsIncomingRequest = new();

    public readonly ToggleSwitch IsIncomingRelayRequest = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public void SendPlanetPosMessage(string text, int planetId, Vector3 pos)
    {
        if (Multiplayer.Session.IsClient) return;
        var planet = GameMain.galaxy.PlanetById(planetId);
        if (planet == null) return;

        var message = text + " [" + NavigateChatLinkHandler.FormatNavigateToPlanetPos(planetId, pos, planet.displayName) + "]";
        ChatManager.Instance.SendChatMessage(message, ChatMessageType.BattleMessage);
        Multiplayer.Session.Network.SendPacket(new NewChatMessagePacket(ChatMessageType.BattleMessage, message));
    }

    public void SendAstroMessage(string text, int astroId0, int astroId1 = 0)
    {
        if (Multiplayer.Session.IsClient) return;

        var message = text + " " + GetAstroLinkText(astroId0);
        if (astroId1 != 0) message += " => " + GetAstroLinkText(astroId1);
        ChatManager.Instance.SendChatMessage(message, ChatMessageType.BattleMessage);
        Multiplayer.Session.Network.SendPacket(new NewChatMessagePacket(ChatMessageType.BattleMessage, message));
    }

    static string GetAstroLinkText(int astroId)
    {
        string displayName = null;
        if (GameMain.galaxy.PlanetById(astroId) != null)
        {
            displayName = GameMain.galaxy.PlanetById(astroId).displayName;
        }
        else if (GameMain.galaxy.StarById(astroId / 100) != null)
        {
            displayName = GameMain.galaxy.StarById(astroId / 100).displayName;
        }
        else if (GameMain.spaceSector.GetHiveByAstroId(astroId) != null)
        {
            displayName = GameMain.spaceSector.GetHiveByAstroId(astroId).displayName;
        }
        if (displayName == null) return "";
        return "[" + NavigateChatLinkHandler.FormatNavigateToAstro(astroId, displayName) + "]";
    }

    public void OnFactoryLoadFinished(PlanetFactory factory)
    {
        var unitCursor = factory.enemySystem.units.cursor;
        var unitBuffer = factory.enemySystem.units.buffer;
        for (var i = 1; i < unitCursor; i++)
        {
            // clear the blocking skill to prevent error due to skills are not all present in client
            unitBuffer[i].ClearBlockSkill();
        }
    }

    public void OnLeavePlanet()
    {
        if (Multiplayer.Session.IsServer) return;

        // Reset threat on each base on the loaded factory so it doesn't show on the monitor        
        for (var factoryIdx = 0; factoryIdx < GameMain.data.factoryCount; factoryIdx++)
        {
            var factory = GameMain.data.factories[factoryIdx];
            if (factory == null) continue;

            var bases = factory.enemySystem.bases;
            for (var baseId = 1; baseId < bases.cursor; baseId++)
            {
                if (bases[baseId] != null && bases[baseId].id == baseId && !bases[baseId].hasAssaultingUnit)
                {
                    bases[baseId].evolve.threat = 0;
                }
            }
        }
    }
}
