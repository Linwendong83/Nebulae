using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic.World;

// Cache only pure functions by the exact arguments the native arithmetic produces.
// Every pose, parent dependency and astro write is still evaluated from current inputs.
// Changed orbital inputs automatically miss the cache if they change the arguments.
internal sealed class PlanetPoseReuse
{
    private bool initialized;
    private double orbitAngle, cosine, sine;
    private float spinAngle;
    private Quaternion spin;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryOrbit(double angle, out double cos, out double sin)
    {
        cos = cosine; sin = sine;
        return initialized && PoseBits.Equal(angle, orbitAngle);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TrySpin(float angle, out Quaternion axis)
    {
        axis = spin;
        return initialized && PoseBits.Equal(angle, spinAngle);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Capture(double angle, double cos, double sin, float rotation, Quaternion axis)
    {
        orbitAngle = angle; cosine = cos; sine = sin; spinAngle = rotation; spin = axis; initialized = true;
    }

    internal void Update(PlanetData p, double time)
    {
        double num = time / p.orbitalPeriod + (double)p.orbitPhase / 360.0;
        int num2 = (int)(num + 0.1);
        num -= (double)num2;
        p.runtimeOrbitPhase = (float)num * 360f;
        num *= Math.PI * 2.0;
        double num3 = time / p.rotationPeriod + (double)p.rotationPhase / 360.0;
        int num4 = (int)(num3 + 0.1);
        num3 = (num3 - (double)num4) * 360.0;
        p.runtimeRotationPhase = (float)num3;
        var orbitHit = TryOrbit(num, out var currentCos, out var currentSin);
        if (!orbitHit) { currentCos = Math.Cos(num); currentSin = Math.Sin(num); }
        VectorLF3 vectorLF = Maths.QRotateLF(p.runtimeOrbitRotation, new VectorLF3(currentCos * (double)p.orbitRadius, 0.0, currentSin * (double)p.orbitRadius));
        if (p.orbitAroundPlanet != null)
        {
            vectorLF.x += p.orbitAroundPlanet.runtimePosition.x;
            vectorLF.y += p.orbitAroundPlanet.runtimePosition.y;
            vectorLF.z += p.orbitAroundPlanet.runtimePosition.z;
        }
        p.runtimePosition = vectorLF;
        var axis = TrySpin((float)num3, out var cachedAxis) ? cachedAxis : Quaternion.AngleAxis((float)num3, Vector3.down);
        p.runtimeRotation = p.runtimeSystemRotation * axis;
        p.uPosition.x = p.star.uPosition.x + vectorLF.x * 40000.0;
        p.uPosition.y = p.star.uPosition.y + vectorLF.y * 40000.0;
        p.uPosition.z = p.star.uPosition.z + vectorLF.z * 40000.0;
        p.runtimeLocalSunDirection = Maths.QInvRotate(p.runtimeRotation, -vectorLF);
        double num5 = time + 1.0 / 60.0;
        double num6 = num5 / p.orbitalPeriod + (double)p.orbitPhase / 360.0;
        int num7 = (int)(num6 + 0.1);
        num6 -= (double)num7;
        num6 *= Math.PI * 2.0;
        double num8 = num5 / p.rotationPeriod + (double)p.rotationPhase / 360.0;
        int num9 = (int)(num8 + 0.1);
        num8 = (num8 - (double)num9) * 360.0;
        var nextCos = Math.Cos(num6); var nextSin = Math.Sin(num6);
        VectorLF3 vectorLF2 = Maths.QRotateLF(p.runtimeOrbitRotation, new VectorLF3(nextCos * (double)p.orbitRadius, 0.0, nextSin * (double)p.orbitRadius));
        if (p.orbitAroundPlanet != null)
        {
            vectorLF2.x += p.orbitAroundPlanet.runtimePositionNext.x;
            vectorLF2.y += p.orbitAroundPlanet.runtimePositionNext.y;
            vectorLF2.z += p.orbitAroundPlanet.runtimePositionNext.z;
        }
        p.runtimePositionNext = vectorLF2;
        var nextAxis = Quaternion.AngleAxis((float)num8, Vector3.down);
        p.runtimeRotationNext = p.runtimeSystemRotation * nextAxis;
        Capture(num6, nextCos, nextSin, (float)num8, nextAxis);
        p.uPositionNext.x = p.star.uPosition.x + vectorLF2.x * 40000.0;
        p.uPositionNext.y = p.star.uPosition.y + vectorLF2.y * 40000.0;
        p.uPositionNext.z = p.star.uPosition.z + vectorLF2.z * 40000.0;
        p.galaxy.astrosData[p.id].uPos = p.uPosition;
        p.galaxy.astrosData[p.id].uRot = p.runtimeRotation;
        p.galaxy.astrosData[p.id].uPosNext = p.uPositionNext;
        p.galaxy.astrosData[p.id].uRotNext = p.runtimeRotationNext;
        p.galaxy.astrosFactory[p.id] = p.factory;
    }
}
