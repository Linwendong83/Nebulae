using NebulaeModel.DataStructures;
using UnityEngine;

namespace NebulaeWorld.Factory;

/// <summary>Runs only native flight/animation math, with private energy and no world outcomes.</summary>
internal sealed class DroneFlightView
{
    public DroneComponent Drone;
    public CraftData Craft;
    public DroneFlightState Command { get; private set; }
    public bool Complete { get; private set; }
    public bool Finished => Complete && Drone.stage == 0;

    public DroneFlightView(in DroneFlightState state, DroneFlightView previous = null)
    {
        Command = state;
        Craft = new CraftData { pos = state.Position, rot = state.Rotation };
        Drone = new DroneComponent
        {
            stage = state.Stage,
            targetObjectId = state.Target,
            movement = state.Movement,
            progress = state.Progress,
            targetPos = state.TargetPosition,
            initialVector = state.InitialVector
        };
        Complete = state.Stage == 0;
        // The host can reuse a base slot before its local return animation finishes.
        // Keep that aircraft in the air and turn toward the new task instead of teleporting it home.
        if (state.Stage > 0 && previous?.Drone.stage > 0)
        {
            Craft = previous.Craft;
            if (((Vector3)Craft.pos - state.EjectPosition).sqrMagnitude > .25f)
            {
                Drone.stage = state.Stage == 4 ? 4 : 2;
                Drone.progress = 0;
            }
        }
    }

    public void Apply(in DroneFlightState state)
    {
        // Position, rotation and takeoff progress belong to local flight after initialization.
        var changedTarget = Command.Target != state.Target || !Command.TargetPosition.Equals(state.TargetPosition);
        Command = state;
        Drone.movement = state.Movement;
        if (state.Stage == 0)
        {
            Complete = true;
            if (Drone.stage != 0) { Drone.stage = 4; Drone.targetObjectId = 0; }
        }
        else if (state.Stage == 4)
        {
            Drone.stage = 4; Drone.targetObjectId = 0;
        }
        else if (changedTarget)
        {
            Drone.targetObjectId = state.Target; Drone.targetPos = state.TargetPosition;
            Drone.stage = 2;
        }
    }

    public void Advance(PlanetFactory factory, Vector3 eject, float dt)
    {
        if (Drone.stage == 0 || dt <= 0) return;
        var target = Drone.targetObjectId;
        // Native flight validates the live entity/prebuild before moving. A visual must still
        // finish its leg when the host built, repaired or removed that object in the meantime.
        if (Drone.stage == 2) Drone.targetObjectId = 0;
        var energy = Command.Powered || Complete ? (double)float.MaxValue : 0;
        var change = 0d;
        var stage = Drone.stage;
        // Work power controls the repair spin without spending any actual energy.
        var workCost = stage == 3 && target > 0 ? 1d : 0d;
        if (workCost > 0) energy = Command.Power;
        var result = Drone.InternalUpdate(ref Craft, factory, ref eject, Command.Speed, dt,
            ref energy, ref change, 0, workCost, out _);
        Drone.targetObjectId = target;
        // Arrival never completes a task or releases a real worker. Only host events do that.
        if (result == 1 && stage == 4 && Complete) Drone.stage = 0;
    }

    public void WritePose(ref CraftData craft, ref AnimData animation, Vector3 eject, int droneId)
    {
        craft.pos = Craft.pos; craft.rot = Craft.rot; craft.vel = Craft.vel;
        animation.state = (uint)Drone.stage;
        animation.power = Drone.stage == 0 ? 0 : Command.Power;
        var distance = Mathf.Min(((Vector3)Craft.pos - eject).magnitude, ((Vector3)Craft.pos - Drone.targetPos).magnitude);
        distance = Mathf.Max(0, distance - 3);
        animation.time = droneId * 256 + (int)((Command.AnimationTime - (int)Command.AnimationTime) * 256) + Mathf.Min(.99f, distance / 20);
    }
}
