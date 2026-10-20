using UnityEngine;

namespace NebulaeWorld.Combat;

/// <summary>Project avatar-local bones into the accepted combat pose, independently of the render origin.</summary>
internal static class RemoteCombatPose
{
    internal readonly struct Frame
    {
        public Frame(Vector3 localPosition, Quaternion localRotation, VectorLF3 universalPosition, Quaternion universalRotation)
        { LocalPosition = localPosition; LocalRotation = localRotation; UniversalPosition = universalPosition; UniversalRotation = universalRotation; }
        public Vector3 LocalPosition { get; }
        public Quaternion LocalRotation { get; }
        public VectorLF3 UniversalPosition { get; }
        public Quaternion UniversalRotation { get; }
    }
    public static bool Owns(Mecha mecha) => Multiplayer.IsActive && Multiplayer.Session.IsServer &&
        mecha?.player?.transform != null && Multiplayer.Session.CombatAuthority.Owns(mecha.player);

    public static Vector3 LocalPoint(Vector3 position, Quaternion rotation, Vector3 offset) => position + rotation * offset;
    public static VectorLF3 UniversalPoint(VectorLF3 position, Quaternion rotation, Vector3 offset) => position + Maths.QRotateLF(rotation, offset);

    private static Vector3 Offset(Mecha mecha, Transform bone) => mecha.player.transform.InverseTransformPoint(bone.position);
    private static Frame Pose(Mecha mecha)
    {
        Multiplayer.Session.CombatAuthority.TryGetPose(mecha, out var frame);
        return frame;
    }

    public static Vector3 LocalBone(Mecha mecha, Transform bone)
    {
        var frame = Pose(mecha);
        return LocalPoint(frame.LocalPosition, frame.LocalRotation, Offset(mecha, bone));
    }
    public static VectorLF3 UniversalBone(Mecha mecha, Transform bone)
    {
        var frame = Pose(mecha);
        return UniversalPoint(frame.UniversalPosition, frame.UniversalRotation, Offset(mecha, bone));
    }

    public static void UpdateColliders(Mecha mecha)
    {
        var a = Offset(mecha, mecha.player.animator.skillTargetCapsuleA);
        var b = Offset(mecha, mecha.player.animator.skillTargetCapsuleB);
        var center = (a + b) * 0.5f;
        var extent = (a - b) * 0.5f;
        var frame = Pose(mecha);
        var localRotation = frame.LocalRotation;
        var identity = 0x1E000000 | Multiplayer.Session.CombatAuthority.PlayerIdFor(mecha);
        mecha.skillColliderL = new ColliderData
        {
            idType = identity,
            pos = LocalPoint(frame.LocalPosition, localRotation, center),
            ext = localRotation * extent,
            radius = 0.85f,
            q = Quaternion.identity
        };
        mecha.skillColliderU = new ColliderDataLF
        {
            idType = identity,
            pos = UniversalPoint(frame.UniversalPosition, frame.UniversalRotation, center),
            ext = Maths.QRotateLF(frame.UniversalRotation, extent),
            radius = 0.85f,
            q = Quaternion.identity
        };
    }
}
