using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NebulaeWorld;
using UnityEngine;

namespace NebulaePatcher.Patches.Dynamic;

// Arithmetic follows the supported native star path. Cache lookups are once per star, rather
// than once per material/transform write; simulation cadence and all visible effects are kept.
[HarmonyPatch(typeof(StarSimulator), nameof(StarSimulator.UpdateUniversalPosition))]
internal static class StarPresentation_Patch
{
    private sealed class Entry
    {
        public readonly Transform Root, Body, Halo, Mass, Black, Atmos, Effect, EffectRenderer, Group;
        private readonly float[] floats = new float[32];
        private readonly bool[] initialized = new bool[32];
        private bool hasLuminosity;
        private float luminosity, adjustedLuminosity;
        public Entry(StarSimulator star)
        {
            Root = star.transform; Body = star.bodyRenderer.transform; Halo = star.haloRenderer.transform;
            Mass = star.massRenderer.transform; Black = star.blackRenderer.transform; Atmos = star.atmosRenderer.transform;
            Effect = star.effect.transform; EffectRenderer = star.effectRenderer.transform;
            if (star.objectGroup != null) Group = star.objectGroup.transform;
        }
        public void Float(int slot, Material material, string property, float value)
        {
            if (initialized[slot] && floats[slot] == value) return;
            material.SetFloat(property, value); floats[slot] = value; initialized[slot] = true;
        }
        private bool flareSet;
        private float flareBrightness;
        public void Brightness(LensFlare flare, float value)
        {
            if (flareSet && flareBrightness == value) return;
            flare.brightness = value; flareBrightness = value; flareSet = true;
        }
        private readonly bool[] enabled = new bool[8], enabledKnown = new bool[8];
        public void Enabled(int slot, Renderer renderer, bool value)
        {
            if (enabledKnown[slot] && enabled[slot] == value) return;
            if (renderer.enabled != value) renderer.enabled = value;
            enabled[slot] = value; enabledKnown[slot] = true;
        }
        public void Enabled(int slot, Behaviour behaviour, bool value)
        {
            if (enabledKnown[slot] && enabled[slot] == value) return;
            if (behaviour.enabled != value) behaviour.enabled = value;
            enabled[slot] = value; enabledKnown[slot] = true;
        }
        public float Luminosity(float value)
        {
            if (!hasLuminosity || luminosity != value)
            { luminosity = value; adjustedLuminosity = value > 1f ? (float)Math.Log(value) + 1f : value; hasLuminosity = true; }
            return adjustedLuminosity;
        }
    }
    private static readonly ConditionalWeakTable<StarSimulator, Entry> states = new();
    internal static void Forget(StarSimulator star) => states.Remove(star);
    private static Entry CreateEntry(StarSimulator star) { VisualWrites.ForgetMaterials(star); return new(star); }

    [HarmonyPrefix]
    internal static bool Prefix(StarSimulator __instance, Vector3 playerLPos, VectorLF3 playerUPos, Vector3 cameraPos, Quaternion cameraRot)
    {
        if (Multiplayer.IsDedicated) return false;
        if (!Multiplayer.IsActive || GameMain.inOtherScene || GameMain.data?.guideMission != null)
        { states.Remove(__instance); VisualWrites.ForgetMaterials(__instance); return true; }
        if (__instance.starData == null) return false;
        var cache = states.GetValue(__instance, CreateEntry);
        var star = __instance.starData;

        if (star == null)
        {
            return false;
        }
        PlanetData localPlanet = GameMain.localPlanet;
        bool flag = star.type == EStarType.GiantStar && star.spectr <= ESpectrType.G;
        VectorLF3 v = star.uPosition;
        if (localPlanet != null)
        {
            v -= localPlanet.uPosition;
            v = Maths.QInvRotateLF(localPlanet.runtimeRotation, v);
        }
        else
        {
            v -= playerUPos;
        }
        v *= (double)__instance.testPosition;
        float vscale = 1f;
        UniverseSimulator.VirtualMapping(v.x, v.y, v.z, cameraPos, out var vpos, out vscale);
        cache.Root.localPosition = vpos;
        __instance.visualScale = vscale;
        double num = Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        float num2 = (float)(num / 40000.0);
        float num3 = (float)(num / 2400000.0);
        __instance.runtimeDist = num;
        Vector3 vector = default(Vector3);
        vector.x = (float)(v.x / num);
        vector.y = (float)(v.y / num);
        vector.z = (float)(v.z / num);
        Vector3 vector2 = cache.Root.TransformDirection(new Vector3(0f, 0f, 1f));
        Vector3 vector3 = vector - vector2;
        if ((double)(vector3.x * vector3.x + vector3.y * vector3.y + vector3.z * vector3.z) > ((num3 > 1f) ? 0.01 : 1E-10))
        {
            cache.Root.forward = vector;
        }
        float num4 = ((float)Math.Log(num2 / star.radius + 1f) - 1f) * 0.5f;
        if (num4 > 1f)
        {
            num4 = 1f;
        }
        else if (num4 < 0f)
        {
            num4 = 0f;
        }
        float num5 = 1f - num4;
        num4 = num4 * num4 * num4;
        float num6 = 20f / (num3 + 3f);
        float num7 = cache.Luminosity(star.luminosity);
        if (num6 > 1f)
        {
            num6 = (float)Math.Log(num6) + 1f;
            num6 = (float)Math.Log(num6) + 1f;
        }

        float num8 = num6 * num7;
        if (num8 < 1f)
        {
            num8 = num8 * 0.5f + 0.5f;
        }
        float num9 = (float)Math.Pow(__instance.visualScale, 0.15);
        num9 *= (float)Math.Pow(num8, 0.4);
        if (star.radius < 1f)
        {
            num9 *= star.radius;
        }
        num8 *= num4;
        cache.Float(0, __instance.haloMaterial, "_Multiplier", num8 * 1.5f);
        cache.Brightness(__instance.sunFlare, num5 * 0.1f);
        float num10 = __instance.visualScale * 6000f * star.radius;
        float num11 = num9 * 3000f;
        float num12 = cache.Body.localScale.x - num10;
        if (num12 > 1f || num12 < -1f)
        {
            if (flag)
            {
                cache.Body.localScale = new Vector3(num10 * 1.4f, num10 * 1.4f, num10 * 1.4f);
            }
            else
            {
                cache.Body.localScale = new Vector3(num10, num10, num10);
            }
        }
        num12 = cache.Halo.localScale.x - num11;
        if (num12 > 1f || num12 < -1f)
        {
            cache.Halo.localScale = new Vector3(num11, num11, num11);
        }
        Quaternion rotation = VisualWrites.Inverse(GameMain.data.relativeRot);
        if (__instance.objectGroup != null)
        {
            cache.Group.rotation = rotation;
            cache.Group.localScale = new Vector3(vscale, vscale, vscale);
        }
        bool flag2 = star.type == EStarType.BlackHole;
        cache.Enabled(0, __instance.bodyRenderer, __instance.visualScale > 0.001f && !flag2);
        cache.Enabled(1, __instance.haloRenderer, num4 > 1E-05f);
        cache.Enabled(2, __instance.atmosRenderer, !flag2);
        cache.Enabled(3, __instance.effectRenderer, !flag2);
        cache.Enabled(4, __instance.sunFlare, num5 > 1E-05f);
        float num13 = 1f;
        if (GameMain.data.guideMission != null)
        {
            num13 = 8f - 7f * Mathf.Clamp01((GameMain.data.guideMission.elapseTime - 40f) / 20f);
        }
        if (GameMain.localStar == star)
        {
            float num14 = 1f;
            float num15 = 0f;
            if (star.type == EStarType.MainSeqStar)
            {
                num14 = num10 * 0.7f * num13;
                num15 = num10 * 0.3f * num13;
            }
            else if (star.type == EStarType.GiantStar)
            {
                num14 = num10 * 6f;
                num15 = num10 * 3f;
            }
            else if (star.type == EStarType.WhiteDwarf)
            {
                num14 = num10 * 12f;
                num15 = num10 * 6f;
            }
            else if (star.type == EStarType.NeutronStar)
            {
                num14 = num10 * 12f;
                num15 = num10 * 6f;
            }
            else
            {
                num14 = num10 * 100f;
                num15 = num10 * 50f;
            }
            float num16 = Mathf.InverseLerp(num14, num15, (float)__instance.runtimeDist);
            cache.Float(1, __instance.bodyMaterial, "_Multiplier", 1f - num16);
            if (star.type == EStarType.BlackHole)
            {
                VisualWrites.Active(__instance.massRenderer.gameObject, false);
                VisualWrites.Active(__instance.atmosRenderer.gameObject, false);
                VisualWrites.Active(__instance.effect.gameObject, false);
                cache.Brightness(__instance.sunFlare, __instance.sunFlare.brightness * num8);
                cache.Enabled(4, __instance.sunFlare, num8 > 0.001f);
            }
            else if (num16 > 0.001f)
            {
                VisualWrites.Active(__instance.massRenderer.gameObject, true);
                VisualWrites.Active(__instance.effect.gameObject, true);
                VisualWrites.Active(__instance.atmosRenderer.gameObject, true);
                cache.Mass.rotation = rotation;
                cache.EffectRenderer.rotation = rotation;
                cache.Float(2, __instance.massMaterial, "_Multiplier", num16 * 4f);
                cache.Float(3, __instance.atmoMaterial, "_Multiplier", num16);
                __instance.atmoMaterial.SetVector("_SunPos", __instance.posVector);
                if (flag)
                {
                    __instance.effectMaterial.SetVector("_SunPos", new Vector4(__instance.posVector.x, __instance.posVector.y, __instance.posVector.z, __instance.posVector.w * 1.1f));
                }
                else
                {
                    __instance.effectMaterial.SetVector("_SunPos", __instance.posVector);
                }
                cache.Float(4, __instance.effectMaterial, "_Intensity", num16);
                if (__instance.effect.isStopped)
                {
                    __instance.effect.Play();
                }
                if (flag)
                {
                    cache.Float(5, __instance.atmoMaterial, "_AtmoThickness", __instance._atmo_param * __instance.solidRadius * 0.04f * 2f);
                    cache.Float(6, __instance.effectMaterial, "_Radius1", __instance._effect_param * __instance.solidRadius * 0.04f * 1.2f);
                }
                else
                {
                    cache.Float(5, __instance.atmoMaterial, "_AtmoThickness", __instance._atmo_param * __instance.solidRadius * 0.04f);
                    cache.Float(6, __instance.effectMaterial, "_Radius1", __instance._effect_param * __instance.solidRadius * 0.04f);
                }
                cache.Mass.localScale = Vector3.one * (__instance.solidRadius * 2f);
                cache.Atmos.position = playerLPos;
                cache.Atmos.rotation = cameraRot;
                cache.Atmos.localScale = Vector3.one * (__instance.solidRadius * 7f);
                cache.Effect.localScale = Vector3.one * (0.04f * __instance.solidRadius);
                cache.Brightness(__instance.sunFlare, __instance.sunFlare.brightness * (1f - num16));
                cache.Enabled(4, __instance.sunFlare, __instance.sunFlare.brightness > 0.001f);
            }
            else
            {
                VisualWrites.Active(__instance.massRenderer.gameObject, false);
                VisualWrites.Active(__instance.atmosRenderer.gameObject, false);
                VisualWrites.Active(__instance.effect.gameObject, false);
            }
            cache.Black.localScale = Vector3.one * (__instance.solidRadius * 2f);
        }
        else
        {
            if (__instance.massRenderer.gameObject.activeSelf)
            {
                VisualWrites.Active(__instance.massRenderer.gameObject, false);
            }
            if (__instance.atmosRenderer.gameObject.activeSelf)
            {
                VisualWrites.Active(__instance.atmosRenderer.gameObject, false);
            }
            if (__instance.effect.gameObject.activeSelf)
            {
                VisualWrites.Active(__instance.effect.gameObject, false);
            }
        }
        __instance.UpdateSunFlareWarpColorShift();

        return false;
    }
}
