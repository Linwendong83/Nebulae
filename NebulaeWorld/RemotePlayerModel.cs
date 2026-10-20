#region

using NebulaeWorld.Combat;
using NebulaeWorld.MonoBehaviours.Remote;
using NebulaeModel.Utils;
using UnityEngine;
using UnityEngine.UI;

#endregion

namespace NebulaeWorld;

internal class RemotePlayerModel
{
    private const int PLAYER_PROTO_ID = 1;

    public RemotePlayerModel(ushort playerId, string username)
    {
        using var wreckageScope = new NebulaeWorld.Combat.RemoteWreckageScope(this);
        // Spawn remote player model by cloning the player prefab and replacing local player script by remote player ones.
        var playerPrefabPath = LDB.players.Select(PLAYER_PROTO_ID).PrefabPath;
        if (playerPrefabPath != null)
        {
            PlayerTransform = Object.Instantiate(Resources.Load<Transform>(playerPrefabPath));
            PlayerModelTransform = PlayerTransform.Find("Model");

            // Remove local player components
            Object.Destroy(PlayerTransform.GetComponent<PlayerFootsteps>());
            Object.Destroy(PlayerTransform.GetComponent<PlayerEffect>());
            Object.Destroy(PlayerTransform.GetComponent<PlayerAudio>());
            // Leave PlayerController to later use
            PlayerTransform.GetComponent<Rigidbody>().isKinematic = true;

            // Add remote player components
            Movement = PlayerTransform.gameObject.AddComponent<RemotePlayerMovement>();
            PlayerTransform.gameObject.AddComponent<RemotePlayerEffects>();
            Animator = PlayerTransform.gameObject.AddComponent<RemotePlayerAnimation>();

            Movement.Username = username;
            Movement.PlayerID = playerId;

            PlayerTransform.GetComponent<PlayerAnimator>().Start();
            PlayerTransform.GetComponent<PlayerAnimator>().enabled = false;
        }

        if (PlayerTransform != null)
        {
            PlayerTransform.gameObject.name = $"Remote Player ({playerId})";

            PlayerInstance = new global::Player();
            PlayerInstance.SetHiddenProperty(nameof(global::Player.transform), PlayerTransform);
        }
        if (Animator != null)
        {
            Animator.PlayerAnimator.player = PlayerInstance;
        }
        MechaInstance = new Mecha();
        if (PlayerInstance != null)
        {
            PlayerInstance.SetHiddenProperty(nameof(global::Player.mecha), MechaInstance);
            MechaInstance.Init(GameMain.data, PlayerInstance);
            MechaInstance.SetForNewGame();
            PlayerInstance.SetHiddenProperty(nameof(global::Player.animator), Animator.PlayerAnimator);

            //Fix MechaArmorModel
            if (PlayerModelTransform != null)
            {
                PlayerInstance.SetHiddenProperty(nameof(global::Player.mechaArmorModel),
                    PlayerModelTransform.GetComponent<MechaArmorModel>());
                var mechaArmorModel = PlayerInstance.mechaArmorModel;
                mechaArmorModel.SetHiddenProperty(nameof(ManualBehaviour.data), PlayerInstance);
                mechaArmorModel.player = PlayerInstance;
                mechaArmorModel.mecha = MechaInstance;
                mechaArmorModel._OnCreate();
                mechaArmorModel._OnInit();

                for (var i = 0; i < PlayerModelTransform.childCount; i++)
                {
                    PlayerModelTransform.GetChild(i).gameObject.SetActive(true);
                }
            }

            PlayerInstance.SetHiddenProperty(nameof(global::Player.controller),
                PlayerTransform.gameObject.GetComponent<PlayerController>());
            var controller = PlayerInstance.controller;
            controller.gameData = GameMain.data;
            controller.player = PlayerInstance;
            controller.mecha = PlayerInstance.mecha;
            controller.model = PlayerModelTransform;
            controller.enabled = false; // Disable updates 
            controller.actionDeath = new PlayerAction_Death();
            controller.actionDeath.Init(PlayerInstance);

            PlayerInstance.isAlive = true;
            var gameObject = new GameObject("Camera Target"); // Dummy object to avoid NRE
            gameObject.transform.SetParent(PlayerInstance.transform, false);
            PlayerInstance.SetHiddenProperty(nameof(global::Player.cameraTarget), gameObject.transform);
        }

        PlayerId = playerId;
        Username = username;
    }

    public string Username { get; set; }
    public ushort PlayerId { get; set; }
    public NebulaeModel.DataStructures.PlayerLifeData Life { get; set; } = new();
    public RemoteRespawnVisualState RespawnVisual { get; } = new();
    public System.Collections.Generic.List<ArmorWreckage> Wreckages { get; set; } = new();
    public Transform PlayerTransform { get; set; }
    public Transform PlayerModelTransform { get; set; }
    public RemotePlayerMovement Movement { get; set; }
    public RemotePlayerAnimation Animator { get; set; }
    public TextMesh InGameNameText { get; set; }
    public Text StarmapNameText { get; set; }
    public Transform StarmapTracker { get; set; }

    public global::Player PlayerInstance { get; set; }
    public Mecha MechaInstance { get; set; }

    public void Destroy()
    {
        Multiplayer.Session.CombatAuthority.PreserveDisconnectedActor(PlayerId);
        using var wreckageScope = new NebulaeWorld.Combat.RemoteWreckageScope(this);
        Object.Destroy(PlayerTransform.gameObject);
        PlayerTransform = null;
        PlayerModelTransform = null;
        Movement = null;
        Animator = null;
        PlayerInstance.Free();
        PlayerInstance = null;
        if (StarmapTracker != null)
        {
            Object.Destroy(StarmapTracker.gameObject);
        }

        if (StarmapNameText != null)
        {
            Object.Destroy(StarmapNameText);
        }

        if (InGameNameText != null)
        {
            Object.Destroy(InGameNameText);
        }

        StarmapTracker = null;
        StarmapNameText = null;
        InGameNameText = null;
    }
}
