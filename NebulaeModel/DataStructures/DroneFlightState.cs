using System.IO;
using UnityEngine;

namespace NebulaeModel.DataStructures;

/// <summary>A flight command and its initial pose. Subsequent commands never correct the displayed pose.</summary>
internal struct DroneFlightState
{
    internal const int WireSize = 93;
    public int Stage, Target, Movement;
    public float Progress, Speed, Power, AnimationTime;
    public bool Powered;
    public Vector3 Position, TargetPosition, InitialVector, EjectPosition;
    public Quaternion Rotation;

    public static DroneFlightState Capture(in DroneComponent drone, in CraftData craft, Vector3 eject,
        float speed, bool powered, float power = 1, float animationTime = 0) => new()
        {
            Stage = drone.stage,
            Target = drone.targetObjectId,
            Movement = drone.movement,
            Progress = drone.progress,
            Position = (Vector3)craft.pos,
            Rotation = drone.stage == 0 ? new Quaternion(0, 0, 0, 1) : craft.rot,
            TargetPosition = drone.targetPos,
            InitialVector = drone.initialVector,
            EjectPosition = eject,
            Speed = speed,
            Powered = powered,
            Power = power,
            AnimationTime = animationTime
        };

    public bool SameCommand(in DroneFlightState other) => Stage == other.Stage && Target == other.Target &&
        Movement == other.Movement && Speed == other.Speed && Powered == other.Powered && Power == other.Power &&
        (Stage == 4 || TargetPosition.Equals(other.TargetPosition));

    public bool Valid => Stage >= 0 && Stage <= 4 && Target != int.MinValue && Movement >= 0 &&
        Finite(Progress) && Progress >= 0 && Progress <= 1 && Finite(Speed) && Speed > 0 &&
        Finite(Power) && Power >= 0 && Power <= 1 && Finite(AnimationTime) &&
        Finite(Position) && Finite(TargetPosition) && Finite(InitialVector) && Finite(EjectPosition) &&
        Finite(Rotation.x) && Finite(Rotation.y) && Finite(Rotation.z) && Finite(Rotation.w) &&
        (double)Rotation.x * Rotation.x + (double)Rotation.y * Rotation.y +
        (double)Rotation.z * Rotation.z + (double)Rotation.w * Rotation.w > 1e-12;

    public byte[] Encode()
    {
        using var stream = new MemoryStream(WireSize);
        using var writer = new BinaryWriter(stream);
        Write(writer);
        return stream.ToArray();
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(Stage); writer.Write(Target); writer.Write(Movement); writer.Write(Progress);
        Write(writer, Position);
        writer.Write(Rotation.x); writer.Write(Rotation.y); writer.Write(Rotation.z); writer.Write(Rotation.w);
        Write(writer, TargetPosition); Write(writer, InitialVector); Write(writer, EjectPosition);
        writer.Write(Speed); writer.Write(Powered); writer.Write(Power); writer.Write(AnimationTime);
    }

    public static DroneFlightState Read(BinaryReader reader) => new()
    {
        Stage = reader.ReadInt32(),
        Target = reader.ReadInt32(),
        Movement = reader.ReadInt32(),
        Progress = reader.ReadSingle(),
        Position = ReadVector(reader),
        Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
        TargetPosition = ReadVector(reader),
        InitialVector = ReadVector(reader),
        EjectPosition = ReadVector(reader),
        Speed = reader.ReadSingle(),
        Powered = reader.ReadBoolean(),
        Power = reader.ReadSingle(),
        AnimationTime = reader.ReadSingle()
    };

    public static bool TryDecode(byte[] bytes, out DroneFlightState state)
    {
        state = default;
        if (bytes?.Length != WireSize) return false;
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        state = Read(reader);
        return state.Valid;
    }

    private static void Write(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
    private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
