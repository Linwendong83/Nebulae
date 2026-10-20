#region

using System;
using NebulaeModel.DataStructures;
using NebulaeModel.Networking;

#endregion

namespace NebulaeModel.Packets.Players;

[NebulaeModel.Networking.PacketId(18338091158334336426UL)]
internal class PlayerMovement
{
    public bool HasFinitePose() => Finite(UPosition.x) && Finite(UPosition.y) && Finite(UPosition.z) &&
        Finite(LocalPlanetPosition.x) && Finite(LocalPlanetPosition.y) && Finite(LocalPlanetPosition.z) &&
        Finite(Rotation.x) && Finite(Rotation.y) && Finite(Rotation.z) &&
        Finite(BodyRotation.x) && Finite(BodyRotation.y) && Finite(BodyRotation.z) &&
        Finite(HorzSpeed) && Finite(VertSpeed) && Finite(BuildArea) && Finite(Turning) && Finite(JumpWeight) && Finite(JumpNormalizedTime) && Finite(MiningWeight) &&
        Finite(VelocityU.x) && Finite(VelocityU.y) && Finite(VelocityU.z) &&
        Finite(VelocityL.x) && Finite(VelocityL.y) && Finite(VelocityL.z) &&
        Math.Abs(UPosition.x) <= 1e12 && Math.Abs(UPosition.y) <= 1e12 && Math.Abs(UPosition.z) <= 1e12 &&
        Math.Abs(LocalPlanetPosition.x) <= 1e9 && Math.Abs(LocalPlanetPosition.y) <= 1e9 && Math.Abs(LocalPlanetPosition.z) <= 1e9 &&
        MovementState >= EMovementState.Walk && MovementState <= EMovementState.Sail;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    [Flags]
    internal enum EFlags : byte
    {
        isGrounded = 1,
        inWater = 2,
        warping = 4,
        hasShield = 8,
        chargeShieldBurst = 16
    }

    public PlayerMovement() { }

    public PlayerMovement(ushort playerId, int localPlanetId, Float3 localPlanetPosition, Double3 uPosition, Float3 rotation,
        Float3 bodyRotation, PlayerAnimator animator)
    {
        // Movement
        PlayerId = playerId;
        LocalPlanetId = localPlanetId;
        LocalPlanetPosition = localPlanetPosition;
        UPosition = uPosition;
        Rotation = rotation;
        BodyRotation = bodyRotation;
        VelocityU = new Double3(animator.player.uVelocity.x, animator.player.uVelocity.y, animator.player.uVelocity.z);
        VelocityL = new Float3(animator.controller.velocity);

        // Animation
        MovementState = animator.movementState;
        HorzSpeed = animator.controller.horzSpeed;
        VertSpeed = animator.controller.vertSpeed;
        Turning = animator.turning;
        JumpWeight = animator.jumpWeight;
        JumpNormalizedTime = animator.jumpNormalizedTime;

        //Compress AnimIndex, assume their values are less than 256
        IdleAnimIndex = (byte)animator.idleAnimIndex;
        MiningAnimIndex = (byte)animator.miningAnimIndex;
        MiningWeight = animator.miningWeight;

        Flags = 0;
        if (animator.controller.actionWalk.isGrounded)
        {
            Flags |= EFlags.isGrounded;
        }
        if (animator.controller.actionDrift.inWater)
        {
            Flags |= EFlags.inWater;
        }
        if (animator.player.warping)
        {
            Flags |= EFlags.warping;
        }
        var mecha = animator.controller.mecha;
        BuildArea = mecha.buildArea;
        ConstructionDroneCount = mecha.constructionModule.droneCount;
        ConstructionDronesEnabled = mecha.constructionModule.droneEnabled &&
                                    mecha.constructionModule.droneConstructEnabled;
        CanLaunchConstructionDrone = animator.player.speed <= 20f;
        if (mecha.energyShieldEnergy > 0)
        {
            Flags |= EFlags.hasShield;
        }
        if (mecha.energyShieldBurstProgress > 0f)
        {
            Flags |= EFlags.chargeShieldBurst;
        }
    }

    // Movement
    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public int LocalPlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public Float3 LocalPlanetPosition { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public Double3 UPosition { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public Float3 Rotation { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public Float3 BodyRotation { get; set; }
    [NebulaeModel.Networking.WireOrder(20)]
    public Double3 VelocityU { get; set; }
    [NebulaeModel.Networking.WireOrder(21)]
    public Float3 VelocityL { get; set; }

    // Animation
    [NebulaeModel.Networking.WireOrder(6)]
    public EMovementState MovementState { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public float HorzSpeed { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public float VertSpeed { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public float Turning { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public float JumpWeight { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public float JumpNormalizedTime { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public byte IdleAnimIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public byte MiningAnimIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public float MiningWeight { get; set; }
    [NebulaeModel.Networking.WireOrder(15)]
    public EFlags Flags { get; set; }
    [NebulaeModel.Networking.WireOrder(16)]
    public float BuildArea { get; set; }
    [NebulaeModel.Networking.WireOrder(17)]
    public int ConstructionDroneCount { get; set; }
    [NebulaeModel.Networking.WireOrder(18)]
    public bool ConstructionDronesEnabled { get; set; }
    [NebulaeModel.Networking.WireOrder(19)]
    public bool CanLaunchConstructionDrone { get; set; }
}
