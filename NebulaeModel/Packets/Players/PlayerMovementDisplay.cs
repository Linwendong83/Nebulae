#region

using System;
using NebulaeModel.DataStructures;

#endregion

namespace NebulaeModel.Packets.Players;

/// <summary>
///     What one player's body is doing, as the host relays it to the players who can see it.
/// </summary>
/// <remarks>
///     <para>
///     A client reports its own movement at full precision in <see cref="PlayerMovement" />, and the host
///     forwards that packet to everyone else. Everything the relay carries is presentation: positions,
///     rotations and animation weights, all of which a remote player only ever interpolates or blends.
///     Quantising those fields costs resolution no eye can resolve and removes most of the bytes.
///     </para>
///     <para>
///     Ground combat also consumes the local velocity from this same movement sample, so turret
///     prediction never borrows the camera player's velocity. Build capability stays host-only.
///     </para>
/// </remarks>
[NebulaeModel.Networking.PacketId(8892736451029384756UL)]
internal class PlayerMovementDisplay
{
    /// <summary>Angles are stored against a full turn, so one step is 360/65536 degrees.</summary>
    public const float EulerTurns = 65535f;

    /// <summary>
    ///     Scale for the horizontal speed. The game caps sail speed at 1000, so this covers the whole
    ///     range with a resolution finer than any animation threshold reads.
    /// </summary>
    public const float SpeedScale = 64f;

    /// <summary>
    ///     Scale for the vertical speed, which is signed: a falling player has a negative one. Half the
    ///     horizontal scale buys the sign bit and still resolves well below the animation thresholds.
    /// </summary>
    public const float VerticalSpeedScale = 32f;

    /// <summary>Encoded value that stands for zero vertical speed.</summary>
    public const ushort VerticalSpeedZero = 32768;

    /// <summary>Vertical speeds the encoding can hold. The two bounds are not symmetric: zero sits above
    /// the middle of the range, so there is slightly more room below it than above it.</summary>
    public const float VerticalSpeedMin = -VerticalSpeedZero / VerticalSpeedScale;

    public const float VerticalSpeedMax = (ushort.MaxValue - VerticalSpeedZero) / VerticalSpeedScale;

    public PlayerMovementDisplay() { }

    public PlayerMovementDisplay(PlayerMovement movement)
    {
        PlayerId = movement.PlayerId;
        LocalPlanetId = movement.LocalPlanetId;
        LocalPlanetPosition = movement.LocalPlanetPosition;
        UPosition = movement.UPosition;
        RotationX = EncodeEuler(movement.Rotation.x);
        RotationY = EncodeEuler(movement.Rotation.y);
        RotationZ = EncodeEuler(movement.Rotation.z);
        BodyRotationX = EncodeEuler(movement.BodyRotation.x);
        BodyRotationY = EncodeEuler(movement.BodyRotation.y);
        BodyRotationZ = EncodeEuler(movement.BodyRotation.z);
        MovementState = (byte)movement.MovementState;
        HorzSpeed = EncodeSpeed(movement.HorzSpeed);
        VertSpeed = EncodeVerticalSpeed(movement.VertSpeed);
        Turning = EncodeTurning(movement.Turning);
        JumpWeight = EncodeWeight(movement.JumpWeight);
        JumpNormalizedTime = EncodeWeight(movement.JumpNormalizedTime);
        IdleAnimIndex = movement.IdleAnimIndex;
        MiningAnimIndex = movement.MiningAnimIndex;
        MiningWeight = EncodeWeight(movement.MiningWeight);
        Flags = movement.Flags;
        VelocityL = movement.VelocityL;
    }

    /// <summary>An angle in degrees against a full turn, clamped into the range it can represent.</summary>
    public static ushort EncodeEuler(float degrees)
    {
        var turns = degrees / 360f;
        if (float.IsNaN(turns))
        {
            return 0;
        }
        turns = turns < 0f ? 0f : turns > 1f ? 1f : turns;
        return (ushort)(turns * EulerTurns);
    }

    public static float DecodeEuler(ushort encoded) => encoded / EulerTurns * 360f;

    /// <summary>A speed against <see cref="SpeedScale" />, clamped into the range it can represent.</summary>
    public static ushort EncodeSpeed(float speed)
    {
        if (float.IsNaN(speed) || speed <= 0f)
        {
            return 0;
        }
        return speed >= ushort.MaxValue / SpeedScale ? ushort.MaxValue : (ushort)(speed * SpeedScale);
    }

    public static float DecodeSpeed(ushort encoded) => encoded / SpeedScale;

    /// <summary>
    ///     A signed vertical speed, stored as an offset from <see cref="VerticalSpeedZero" /> so a falling
    ///     player keeps the sign rather than being clamped to a standstill.
    /// </summary>
    public static ushort EncodeVerticalSpeed(float speed)
    {
        if (float.IsNaN(speed))
        {
            return VerticalSpeedZero;
        }
        var clamped = speed < VerticalSpeedMin ? VerticalSpeedMin
            : speed > VerticalSpeedMax ? VerticalSpeedMax : speed;
        return (ushort)(VerticalSpeedZero + clamped * VerticalSpeedScale);
    }

    public static float DecodeVerticalSpeed(ushort encoded) =>
        (encoded - VerticalSpeedZero) / VerticalSpeedScale;

    /// <summary>A turn rate in [-1, 1] against the full unsigned range.</summary>
    public static ushort EncodeTurning(float turning)
    {
        if (float.IsNaN(turning))
        {
            return EncodeTurning(0f);
        }
        var clamped = turning < -1f ? -1f : turning > 1f ? 1f : turning;
        return (ushort)((clamped + 1f) * 0.5f * EulerTurns);
    }

    public static float DecodeTurning(ushort encoded) => encoded / EulerTurns * 2f - 1f;

    /// <summary>A weight in [0, 1] against the full byte range.</summary>
    public static byte EncodeWeight(float weight)
    {
        if (float.IsNaN(weight) || weight <= 0f)
        {
            return 0;
        }
        return weight >= 1f ? byte.MaxValue : (byte)(weight * byte.MaxValue);
    }

    public static float DecodeWeight(byte encoded) => encoded / (float)byte.MaxValue;

    public Float3 Rotation => new(DecodeEuler(RotationX), DecodeEuler(RotationY), DecodeEuler(RotationZ));

    public Float3 BodyRotation =>
        new(DecodeEuler(BodyRotationX), DecodeEuler(BodyRotationY), DecodeEuler(BodyRotationZ));

    public float HorzSpeedValue => DecodeSpeed(HorzSpeed);

    public float VertSpeedValue => DecodeVerticalSpeed(VertSpeed);

    public float TurningValue => DecodeTurning(Turning);

    public float JumpWeightValue => DecodeWeight(JumpWeight);

    public float JumpNormalizedTimeValue => DecodeWeight(JumpNormalizedTime);

    public float MiningWeightValue => DecodeWeight(MiningWeight);

    /// <summary>
    ///     The pose must be finite, inside the bounds a position can hold, and in a movement state the
    ///     game defines. Whether the named planet exists is a question about the world, not about the
    ///     wire, so the processor asks it separately.
    /// </summary>
    public bool HasFinitePose()
    {
        return Finite(LocalPlanetPosition.x) && Finite(LocalPlanetPosition.y) && Finite(LocalPlanetPosition.z) &&
               Finite(VelocityL.x) && Finite(VelocityL.y) && Finite(VelocityL.z) &&
               Finite(UPosition.x) && Finite(UPosition.y) && Finite(UPosition.z) &&
               Math.Abs(UPosition.x) <= 1e12 && Math.Abs(UPosition.y) <= 1e12 && Math.Abs(UPosition.z) <= 1e12 &&
               Math.Abs(LocalPlanetPosition.x) <= 1e9 && Math.Abs(LocalPlanetPosition.y) <= 1e9 &&
               Math.Abs(LocalPlanetPosition.z) <= 1e9 &&
               MovementState >= (byte)EMovementState.Walk && MovementState <= (byte)EMovementState.Sail;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    [NebulaeModel.Networking.WireOrder(0)]
    public ushort PlayerId { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int LocalPlanetId { get; set; }

    [NebulaeModel.Networking.WireOrder(2)]
    public Float3 LocalPlanetPosition { get; set; }

    [NebulaeModel.Networking.WireOrder(3)]
    public Double3 UPosition { get; set; }

    [NebulaeModel.Networking.WireOrder(4)]
    public ushort RotationX { get; set; }

    [NebulaeModel.Networking.WireOrder(5)]
    public ushort RotationY { get; set; }

    [NebulaeModel.Networking.WireOrder(6)]
    public ushort RotationZ { get; set; }

    [NebulaeModel.Networking.WireOrder(7)]
    public ushort BodyRotationX { get; set; }

    [NebulaeModel.Networking.WireOrder(8)]
    public ushort BodyRotationY { get; set; }

    [NebulaeModel.Networking.WireOrder(9)]
    public ushort BodyRotationZ { get; set; }

    [NebulaeModel.Networking.WireOrder(10)]
    public byte MovementState { get; set; }

    [NebulaeModel.Networking.WireOrder(11)]
    public ushort HorzSpeed { get; set; }

    [NebulaeModel.Networking.WireOrder(12)]
    public ushort VertSpeed { get; set; }

    [NebulaeModel.Networking.WireOrder(13)]
    public ushort Turning { get; set; }

    [NebulaeModel.Networking.WireOrder(14)]
    public byte JumpWeight { get; set; }

    [NebulaeModel.Networking.WireOrder(15)]
    public byte JumpNormalizedTime { get; set; }

    [NebulaeModel.Networking.WireOrder(16)]
    public byte IdleAnimIndex { get; set; }

    [NebulaeModel.Networking.WireOrder(17)]
    public byte MiningAnimIndex { get; set; }

    [NebulaeModel.Networking.WireOrder(18)]
    public byte MiningWeight { get; set; }

    [NebulaeModel.Networking.WireOrder(19)]
    public PlayerMovement.EFlags Flags { get; set; }

    [NebulaeModel.Networking.WireOrder(20)]
    public Float3 VelocityL { get; set; }
}
