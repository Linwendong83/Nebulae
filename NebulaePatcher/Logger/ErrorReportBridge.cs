using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using NebulaeModel;
using NebulaeModel.Logger;
using NebulaeWorld;
using UnityEngine;
using Config = NebulaeModel.Config;

namespace NebulaePatcher.Logger;

// This component stays active even if plugin startup disables NebulaePlugin.
internal sealed class ErrorReportBridge : MonoBehaviour, ILogListener
{
    private ManualLogSource source;
    private ErrorReporter reporter;
    private float refreshed;

    internal void Initialize(ManualLogSource logSource)
    {
        source = logSource;
        reporter = new ErrorReporter(Path.Combine(Paths.BepInExRootPath, "Nebulae", "Reports", "pending"),
            new HttpReportTransport(), message => source.LogWarning(message));
        RefreshEnvironment();
        reporter.SetEnabled(Config.Options.AutoReportErrors);
        BepInEx.Logging.Logger.Listeners.Add(this);
        Application.logMessageReceivedThreaded += OnUnityLog;
        Config.OnConfigApplied += ApplyOptions;
    }

    private static string CurrentRole()
    {
        var session = Multiplayer.Session;
        if (session != null) return Multiplayer.IsDedicated ? "dedicated" : session.IsServer ? "host" : "client";
        return Multiplayer.IsDedicated ? "startup" : "menu";
    }

    private void Update()
    {
        if (reporter == null || Time.unscaledTime - refreshed < 1) return;
        refreshed = Time.unscaledTime;
        try { RefreshEnvironment(); }
        catch (Exception) { /* Metadata refresh must not affect game simulation. */ }
    }

    internal void RefreshEnvironment()
    {
        // Read Unity/game APIs only here, on the main thread. Callbacks only read managed session fields.
        reporter.SetEnvironment(new ReportEnvironment
        {
            NebulaeVersion = PluginInfo.PLUGIN_DISPLAY_VERSION,
            GameVersion = GameConfig.gameVersion.Build == 0 ? DSPGameVersion.VERSION : GameConfig.gameVersion.ToFullString(),
            Runtime = Environment.OSVersion + "; CLR " + Environment.Version,
            Role = CurrentRole(),
            Mods = Chainloader.PluginInfos.Values.Select(p => p.Metadata.GUID + " " + p.Metadata.Version).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            Secrets = ServerMemoryStore.Instance.Servers.Select(s => s.Password)
                .Concat(new[] { Config.Options.ServerPassword, Multiplayer.LastConnection?.Password }).ToArray()
        });
    }

    private void ApplyOptions()
    {
        reporter.SetEnabled(Config.Options.AutoReportErrors);
        try { RefreshEnvironment(); }
        catch (Exception) { /* A settings change still takes effect if metadata is unavailable. */ }
    }

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.Source, source) || reporter == null) return;
        try
        {
            var message = eventArgs.Data?.ToString() ?? "";
            if (message.StartsWith("[error-reporter]", StringComparison.Ordinal)) return;
            var isError = (eventArgs.Level & (LogLevel.Error | LogLevel.Fatal)) != 0;
            // Debug logging is often per tick and does not belong in the context ring.
            if (!isError && (eventArgs.Level & (LogLevel.Info | LogLevel.Warning | LogLevel.Message)) == 0) return;
            RememberCurrentSecrets();
            reporter.Capture("Nebulae", message, "", isError, role: CurrentRole());
        }
        catch (Exception) { /* Reporting must never interrupt the original log consumer. */ }
    }

    private void OnUnityLog(string message, string stack, LogType type)
    {
        if (type is not (LogType.Error or LogType.Assert or LogType.Exception) || reporter == null || !Multiplayer.IsActive) return;
        // BepInEx may forward our own log back to Unity; it was already captured at its source.
        if (message != null && (message.StartsWith("[error-reporter]", StringComparison.Ordinal) ||
            message.StartsWith("[Error  :   Nebulae]", StringComparison.Ordinal))) return;
        try
        {
            RememberCurrentSecrets();
            reporter.Capture("Unity", message, stack, true, unity: true, role: CurrentRole());
        }
        catch (Exception) { /* Unity invokes this callback from worker threads too. */ }
    }

    private void RememberCurrentSecrets()
    {
        // Managed strings are safe to read on a logging thread, including the frame of joining.
        reporter.RememberSecret(Config.Options?.ServerPassword);
        reporter.RememberSecret(Multiplayer.LastConnection?.Password);
    }

    private void OnApplicationQuit() => Dispose();
    private void OnDestroy() => Dispose();
    public void Dispose()
    {
        if (reporter == null) return;
        Application.logMessageReceivedThreaded -= OnUnityLog;
        BepInEx.Logging.Logger.Listeners.Remove(this);
        Config.OnConfigApplied -= ApplyOptions;
        reporter.Dispose();
        reporter = null;
    }
}
