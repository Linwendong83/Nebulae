#region

using NebulaeModel.DataStructures;
using NebulaeModel.Networking;
using NebulaeModel.Packets.Players;
using UnityEngine;

#endregion

namespace NebulaeWorld.MonoBehaviours.Local;

public class LocalPlayerMovement : MonoBehaviour
{
    public const int SEND_RATE = 10;
    private const float BROADCAST_INTERVAL = 1f / SEND_RATE;

    /// <summary>
    ///     How often the pose is sent even when nothing about it has changed, in seconds.
    /// </summary>
    /// <remarks>
    ///     A player who is standing still has a pose that stops changing, and sending it ten times a
    ///     second says nothing new. This floor keeps the star map and any late joiner converging without
    ///     asking the idle players to keep talking.
    /// </remarks>
    private const float HEARTBEAT_INTERVAL = 1f;

    // Below these, a change in the field is not something a remote player could see. The pose is sent in
    // full when any one of them is exceeded.
    private const float PositionEpsilon = 0.01f;
    private const float UniversalPositionEpsilon = 0.05f;
    private const float AngleEpsilon = 0.25f;
    private const float SpeedEpsilon = 0.05f;
    private const float WeightEpsilon = 0.01f;

    private Transform bodyTransform;
    private PlayerAnimator playerAnimator;
    private Transform rootTransform;

    private float time;
    private float timeSinceLastSend;

    // The pose as it was last put on the wire, so the next one can be compared against it.
    private Float3 lastLocalPosition;
    private Double3 lastUPosition;
    private Float3 lastRotation;
    private Float3 lastBodyRotation;
    private float lastHorzSpeed;
    private float lastVertSpeed;
    private float lastTurning;
    private float lastJumpWeight;
    private float lastJumpNormalizedTime;
    private float lastMiningWeight;
    private int lastIdleAnimIndex;
    private int lastMiningAnimIndex;
    private int lastFlags;
    private int lastMovementState;
    private int lastLocalPlanetId;
    private bool hasLastPose;
    private readonly PlayerMovementRelay hostRelay = new();

    private void Awake()
    {
        rootTransform = GetComponent<Transform>();
        bodyTransform = rootTransform.Find("Model");
        playerAnimator = GetComponent<PlayerAnimator>();
    }

    private void Update()
    {
        if (!Multiplayer.Session.JoinSync.CanInteract) return;
        // update navigation indicator
        Multiplayer.Session.Gizmos.OnUpdate();

        time += Time.deltaTime;
        timeSinceLastSend += Time.deltaTime;

        if (!(time >= BROADCAST_INTERVAL))
        {
            return;
        }
        // Carry the remainder instead of dropping it, so the send rate does not drift with the frame rate.
        time -= BROADCAST_INTERVAL;

        Publish();
    }

    public void Publish()
    {
        if (!Multiplayer.Session.JoinSync.CanInteract) return;
        var rotation = new Float3(rootTransform.eulerAngles);
        var bodyRotation = new Float3(bodyTransform.eulerAngles);

        var uPosition = new Double3(GameMain.mainPlayer.uPosition.x, GameMain.mainPlayer.uPosition.y,
            GameMain.mainPlayer.uPosition.z);
        var position = rootTransform.position;
        var localPlanetId = GameMain.localPlanet?.id ?? -1;

        if (hasLastPose && timeSinceLastSend < HEARTBEAT_INTERVAL && !HasChanged(position, uPosition, rotation,
                bodyRotation, localPlanetId, playerAnimator))
        {
            // Nothing a remote player could observe moved, so this tick costs nothing.
            return;
        }
        timeSinceLastSend = 0f;

        var sample = new PlayerMovement(Multiplayer.Session.LocalPlayer.Id,
            localPlanetId, position.ToFloat3(), uPosition, rotation, bodyRotation,
            playerAnimator);
        Multiplayer.Session.Combat.RecordLocalMovement(sample, GameMain.localStar?.id ?? -1);
        // Clients consume the host's display protocol; the full movement packet is
        // admitted only by the host, so broadcasting it from a listen host loses its pose.
        if (Multiplayer.Session.IsServer)
            hostRelay.Send(Multiplayer.Session.Network, new PlayerMovementDisplay(sample),
                GameMain.localStar?.id ?? -1, Time.time);
        else Multiplayer.Session.Network.SendPacket(sample);

        var playerData = Multiplayer.Session.LocalPlayer.Data;
        playerData.BodyRotation = bodyRotation;
        playerData.LocalPlanetId = localPlanetId;
        playerData.LocalPlanetPosition = position.ToFloat3();
        playerData.LocalStarId = GameMain.localStar?.id ?? -1;
        playerData.Rotation = rotation;
        playerData.UPosition = uPosition;

        Remember(position, uPosition, rotation, bodyRotation, localPlanetId, playerAnimator);
    }

    private bool HasChanged(Vector3 position, Double3 uPosition, Float3 rotation, Float3 bodyRotation,
        int localPlanetId, PlayerAnimator animator)
    {
        if (localPlanetId != lastLocalPlanetId)
        {
            return true;
        }
        if (Distance(position.ToFloat3(), lastLocalPosition) > PositionEpsilon)
        {
            return true;
        }
        if (Distance(uPosition, lastUPosition) > UniversalPositionEpsilon)
        {
            return true;
        }
        if (AngleDistance(rotation, lastRotation) > AngleEpsilon ||
            AngleDistance(bodyRotation, lastBodyRotation) > AngleEpsilon)
        {
            return true;
        }
        if (Mathf.Abs(animator.controller.horzSpeed - lastHorzSpeed) > SpeedEpsilon ||
            Mathf.Abs(animator.controller.vertSpeed - lastVertSpeed) > SpeedEpsilon ||
            Mathf.Abs(animator.turning - lastTurning) > SpeedEpsilon)
        {
            return true;
        }
        if (Mathf.Abs(animator.jumpWeight - lastJumpWeight) > WeightEpsilon ||
            Mathf.Abs(animator.jumpNormalizedTime - lastJumpNormalizedTime) > WeightEpsilon ||
            Mathf.Abs(animator.miningWeight - lastMiningWeight) > WeightEpsilon)
        {
            return true;
        }
        if (animator.idleAnimIndex != lastIdleAnimIndex || animator.miningAnimIndex != lastMiningAnimIndex)
        {
            return true;
        }
        return FlagsOf(animator) != lastFlags || (int)animator.movementState != lastMovementState;
    }

    private void Remember(Vector3 position, Double3 uPosition, Float3 rotation, Float3 bodyRotation,
        int localPlanetId, PlayerAnimator animator)
    {
        lastLocalPosition = position.ToFloat3();
        lastUPosition = uPosition;
        lastRotation = rotation;
        lastBodyRotation = bodyRotation;
        lastLocalPlanetId = localPlanetId;
        lastHorzSpeed = animator.controller.horzSpeed;
        lastVertSpeed = animator.controller.vertSpeed;
        lastTurning = animator.turning;
        lastJumpWeight = animator.jumpWeight;
        lastJumpNormalizedTime = animator.jumpNormalizedTime;
        lastMiningWeight = animator.miningWeight;
        lastIdleAnimIndex = animator.idleAnimIndex;
        lastMiningAnimIndex = animator.miningAnimIndex;
        lastFlags = FlagsOf(animator);
        lastMovementState = (int)animator.movementState;
        hasLastPose = true;
    }

    private static int FlagsOf(PlayerAnimator animator)
    {
        var flags = 0;
        if (animator.controller.actionWalk.isGrounded)
        {
            flags |= 1;
        }
        if (animator.controller.actionDrift.inWater)
        {
            flags |= 2;
        }
        if (animator.player.warping)
        {
            flags |= 4;
        }
        var mecha = animator.controller.mecha;
        if (mecha.energyShieldEnergy > 0)
        {
            flags |= 8;
        }
        if (mecha.energyShieldBurstProgress > 0f)
        {
            flags |= 16;
        }
        return flags;
    }

    private static float Distance(Float3 a, Float3 b)
    {
        var dx = a.x - b.x;
        var dy = a.y - b.y;
        var dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float Distance(Double3 a, Double3 b)
    {
        var dx = a.x - b.x;
        var dy = a.y - b.y;
        var dz = a.z - b.z;
        return Mathf.Sqrt((float)(dx * dx + dy * dy + dz * dz));
    }

    /// <summary>
    ///     The largest angle any one of the three components moved, in degrees.
    /// </summary>
    private static float AngleDistance(Float3 a, Float3 b)
    {
        return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));
    }
}
