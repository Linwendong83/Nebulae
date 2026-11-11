using System;
using System.Runtime.CompilerServices;
using NebulaeWorld;
using NebulaeWorld.Authority;
using UnityEngine;
using UnityEngine.UI;

namespace NebulaePatcher.Patches.Dynamic.UI;

// Presentation only. Never ease the authoritative resource, research or production counters.
internal static class SynchronizedProgress
{
    internal readonly struct Context
    {
        public readonly object Source;
        public readonly int Id, Variant;
        public readonly long Capacity;

        public Context(object source, int id = 0, int variant = 0, long capacity = 0)
        {
            Source = source; Id = id; Variant = variant; Capacity = capacity;
        }

        public bool Matches(Context other) => ReferenceEquals(Source, other.Source) &&
            Id == other.Id && Variant == other.Variant && Capacity == other.Capacity;
    }

    internal sealed class Display
    {
        private object session;
        private Context context;
        private float value, target;
        private int frame = -1;

        internal float Update(float raw, Context nextContext, object nextSession, int nextFrame,
            float deltaTime, bool explicitTarget)
        {
            var reset = frame < 0 || !ReferenceEquals(session, nextSession) ||
                !context.Matches(nextContext) || nextFrame - frame > 1;
            // Some native refreshes skip tiny changes. Keep settling toward the last target,
            // rather than feeding the displayed intermediate value back into the filter.
            if (explicitTarget || raw != value || reset) target = raw;
            value = reset ? raw : frame == nextFrame ? value : Ease(value, target, deltaTime);
            frame = nextFrame;
            context = nextContext;
            session = nextSession;
            return value;
        }
    }

    private static readonly ConditionalWeakTable<Image, Display> displays = new();

    internal static float Ease(float current, float target, float deltaTime)
    {
        // Same response as the mecha weapon bars: 95% settled in about 0.125 seconds.
        var value = current + (target - current) * (1f - (float)Math.Exp(-24f * Math.Max(0f, deltaTime)));
        return Math.Abs(value - target) < 0.0001f ? target : value;
    }

    internal static void Reset(Image image)
    {
        if (image != null) displays.Remove(image);
    }

    internal static void Apply(Image image, Context context, float? target = null)
    {
        if (image == null) return;
        if (!WorldSimulation.IsClient || !image.isActiveAndEnabled)
        {
            displays.Remove(image);
            return;
        }

        var raw = target ?? image.fillAmount;
        var display = displays.GetValue(image, _ => new Display());
        image.fillAmount = display.Update(raw, context, Multiplayer.Session, Time.frameCount,
            Time.unscaledDeltaTime, target.HasValue);
    }

    internal static Context Research(int techId, int levelOffset = 0)
    {
        var history = GameMain.history;
        var state = history != null && techId > 0 ? history.TechState(techId) : default;
        return new Context(history, techId, state.curLevel + levelOffset, state.hashNeeded);
    }
}
