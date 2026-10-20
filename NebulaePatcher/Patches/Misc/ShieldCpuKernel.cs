using System;
using UnityEngine;

namespace NebulaePatcher.Patches.Misc;

// CPU transcription of the game's cs_5_0 shield kernel.
// Five surface samples, ordered smooth union of generators, finite-difference normals,
// underside contraction and both quantized counters are preserved. No coverage constants.
internal static class ShieldCpuKernel
{
    internal static void Validate(Vector3[] source, Vector4[] generators, int count, float radius,
        float altitude, float scale, float blend, Vector3[] vertices, Vector3[] normals, uint[] args)
    {
        if (source == null || source.Length == 0 || source.Length > 65536 || vertices == null ||
            normals == null || vertices.Length < source.Length || normals.Length < source.Length ||
            args == null || args.Length < 10 || count < 0 || count > 80 ||
            (count > 0 && (generators == null || generators.Length < count)))
            throw new ArgumentException("Invalid planetary shield buffer dimensions.");
        if (!PositiveFinite(radius) || !PositiveFinite(altitude) || !PositiveFinite(scale) || !PositiveFinite(blend))
            throw new ArgumentException("Shield radius, altitude, physics scale and blend must be finite and positive.");
        for (var i = 0; i < source.Length; i++)
            if (!Finite(source[i].x) || !Finite(source[i].y) || !Finite(source[i].z) || source[i].sqrMagnitude < 1e-20f)
                throw new ArgumentException("Invalid shield source vertex.");
        for (var i = 0; i < count; i++)
            if (!Finite(generators[i].x) || !Finite(generators[i].y) || !Finite(generators[i].z) || !Finite(generators[i].w))
                throw new ArgumentException("Invalid shield generator data.");
    }

    internal static void Compute(Vector3[] source, Vector4[] generators, int count, float radius,
        float altitude, float scale, float blend, Vector3[] vertices, Vector3[] normals, uint[] args)
    {
        Validate(source, generators, count, radius, altitude, scale, blend, vertices, normals, args);
        Array.Clear(args, 0, args.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var radial = Normalize(source[i]);
            var tangent = Math.Abs(radial.y) < 0.999999f
                ? Normalize(new Vector3(-radial.z, 0, radial.x))
                : Normalize(new Vector3(radial.y, -radial.x, 0));
            var bitangent = Normalize(Vector3.Cross(tangent, radial));
            var point = radial * radius;
            var tm = Sample(point - tangent * 12.5f, generators, count, radius, blend);
            var tp = Sample(point + tangent * 12.5f, generators, count, radius, blend);
            var bm = Sample(point - bitangent * 12.5f, generators, count, radius, blend);
            var bp = Sample(point + bitangent * 12.5f, generators, count, radius, blend);
            var center = Sample(point, generators, count, radius, blend);

            var position = point + radial * altitude * center;
            var normal = Normalize(radial + tangent * ((altitude * tm - altitude * tp) * 0.04f)
                + bitangent * ((altitude * bm - altitude * bp) * 0.04f));
            if (Length(position) < radius + 4.5f) position *= 0.96f;
            var solidity = Saturate((Length(position) - (radius - 5f)) * 0.1f);
            // Keep the shader's operation order, including altitude scaling before averaging.
            var neighbourhood = altitude * tm + altitude * tp;
            neighbourhood = altitude * bm + neighbourhood;
            neighbourhood = altitude * bp + neighbourhood;
            var coverage = center * 0.5f + neighbourhood / altitude * 0.125f;
            vertices[i] = position * scale;
            normals[i] = normal;
            args[0] += (uint)(Saturate(coverage * 1.005f - 0.0025f) * 1000f + 0.5f);
            args[1] += (uint)(solidity * 1000f + 0.5f);
        }
    }

    private static float Sample(Vector3 point, Vector4[] generators, int count, float radius, float blend)
    {
        var total = 0f;
        var halfRadius = radius * 0.5f;
        for (var i = 0; i < count; i++)
        {
            var g = generators[i];
            if (g.w < 0.001f) continue;
            var delta = new Vector3(point.x - g.x, point.y - g.y, point.z - g.z);
            var distance = Length(delta) / (halfRadius * g.w);
            var quadratic = 1f - distance * distance;
            var shape = quadratic + Saturate(distance) * ((1f - distance) - quadratic);
            var contribution = shape * g.w;
            var width = (g.w * 0.25f + 0.75f) * blend;
            var h = Saturate(0.5f - (contribution - total) * 0.5f / width);
            total = contribution + h * (total - contribution) + width * h * (1f - h);
        }
        total = Saturate(total);
        var smooth = total * total * (3f - 2f * total);
        return smooth * (smooth * smooth);
    }

    private static float Length(Vector3 v) => (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
    private static Vector3 Normalize(Vector3 v) => v * (1f / Length(v));
    private static float Saturate(float value) => value < 0 ? 0 : value > 1 ? 1 : value;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool PositiveFinite(float value) => value > 0 && Finite(value);
}
