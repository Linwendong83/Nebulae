using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using NebulaeModel.Logger;

namespace NebulaeWorld.Authority;

/// <summary>
/// Opt-in cost breakdown of the client's replica frame.
/// </summary>
/// <remarks>
/// <para>
/// Set <c>NEBULAE_AUTHORITY_PROFILE=1</c> to record. When the flag is unset every entry point is a
/// single branch and no timestamp is taken, so a normal session pays nothing for the instrument.
/// One JSON line per report window is appended to <c>nebulae-authority-profile.jsonl</c> in the
/// game root; the section call counts are also the record and shell volumes of the same window.
/// </para>
/// <para>
/// This is observation only. Nothing here changes what is applied, when it is applied, or which
/// object is presented — the timers wrap the calls the frame already made.
/// </para>
/// </remarks>
internal static class AuthorityProfile
{
    internal const int Frame = 0;
    internal const int Drain = 1;
    internal const int GroundApply = 2;
    internal const int PoolWrite = 3;
    internal const int ShellCreate = 4;
    internal const int ShellRemove = 5;
    internal const int PrepareGround = 6;
    internal const int PrepareSpace = 7;
    internal const int Search = 8;
    internal const int Flush = 9;
    internal const int AfterFrame = 10;
    internal const int Reapply = 11;
    private const int SectionCount = 12;

    private static readonly string[] Names =
    {
        "frame", "drain", "groundApply", "poolWrite", "shellCreate", "shellRemove",
        "prepareGround", "prepareSpace", "search", "flushPose", "afterFrame", "reapply"
    };

    /// <summary>True when the environment asked for a profile.</summary>
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("NEBULAE_AUTHORITY_PROFILE") == "1";

    private const double ReportSeconds = 10;

    private static readonly long[] sectionTicks = new long[SectionCount];
    private static readonly long[] sectionMax = new long[SectionCount];
    private static readonly long[] sectionCalls = new long[SectionCount];

    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static double windowStarted;
    private static long windowFrames;
    private static long windowTicks;
    private static long windowBaselines;
    private static int windowGc0;
    private static bool announced;

    /// <summary>Starts one section, or returns zero when the profile is off.</summary>
    internal static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>Adds one call to a section.</summary>
    internal static void End(int section, long started)
    {
        if (!Enabled || started == 0L) return;
        var elapsed = Stopwatch.GetTimestamp() - started;
        sectionCalls[section]++;
        sectionTicks[section] += elapsed;
        if (elapsed > sectionMax[section]) sectionMax[section] = elapsed;
    }

    /// <summary>Counts one installed baseline; a re-baseline storm shows up here, not in a section.</summary>
    internal static void BaselineInstalled()
    {
        if (Enabled) windowBaselines++;
    }

    /// <summary>Ends one drained frame and writes a report when the window is full.</summary>
    internal static void FrameCompleted(long tick)
    {
        if (!Enabled) return;
        if (!announced)
        {
            announced = true;
            Log.Info("[authority-profile] recording to " + Path.Combine(Paths.GameRootPath, "nebulae-authority-profile.jsonl"));
            windowStarted = clock.Elapsed.TotalSeconds;
            windowGc0 = GC.CollectionCount(0);
        }
        windowFrames++;
        windowTicks = tick;
        var now = clock.Elapsed.TotalSeconds;
        if (now - windowStarted < ReportSeconds) return;
        Write(now - windowStarted);
        Reset(now);
    }

    private static void Write(double seconds)
    {
        try
        {
            var text = new StringBuilder(1024);
            text.Append('{');
            text.Append("\"utc\":\"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",");
            text.Append("\"seconds\":").Append(Number(seconds)).Append(',');
            text.Append("\"frames\":").Append(windowFrames).Append(',');
            text.Append("\"tick\":").Append(windowTicks).Append(',');
            text.Append("\"ups\":").Append(Number(FPSController.currentUPS)).Append(',');
            text.Append("\"fps\":").Append(Number(FPSController.currentFPS)).Append(',');
            text.Append("\"gc0\":").Append(GC.CollectionCount(0) - windowGc0).Append(',');
            var factory = GameMain.localPlanet?.factory;
            text.Append("\"planet\":").Append(GameMain.localPlanet?.id ?? 0).Append(',');
            text.Append("\"enemyCursor\":").Append(factory?.enemyCursor ?? 0).Append(',');
            text.Append("\"enemyUnits\":").Append(factory?.enemySystem?.units.count ?? 0).Append(',');
            text.Append("\"baselines\":").Append(windowBaselines).Append(',');
            text.Append("\"sections\":[");
            for (var i = 0; i < SectionCount; i++)
            {
                if (i > 0) text.Append(',');
                var calls = sectionCalls[i];
                var milliseconds = Milliseconds(sectionTicks[i]);
                text.Append("{\"name\":\"").Append(Names[i]).Append("\",");
                text.Append("\"calls\":").Append(calls).Append(',');
                text.Append("\"totalMs\":").Append(Number(milliseconds)).Append(',');
                text.Append("\"maxMs\":").Append(Number(Milliseconds(sectionMax[i]))).Append(',');
                text.Append("\"usPerCall\":").Append(Number(calls > 0 ? milliseconds * 1000.0 / calls : 0)).Append('}');
            }
            text.Append("]}");
            File.AppendAllText(Path.Combine(Paths.GameRootPath, "nebulae-authority-profile.jsonl"),
                text.ToString() + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A diagnostic that cannot write must never disturb the frame it is describing.
        }
    }

    private static void Reset(double now)
    {
        Array.Clear(sectionTicks, 0, SectionCount);
        Array.Clear(sectionMax, 0, SectionCount);
        Array.Clear(sectionCalls, 0, SectionCount);
        windowFrames = 0;
        windowBaselines = 0;
        windowGc0 = GC.CollectionCount(0);
        windowStarted = now;
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Number(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? "0" : value.ToString("R", CultureInfo.InvariantCulture);
}
