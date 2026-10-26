#region

using System;
using System.Text.RegularExpressions;
using WebSocketSharp;

#endregion

namespace NebulaeModel.Logger;

internal static class Log
{
    private static ILogger logger;
    public static string LastErrorMsg { get; set; }
    public static int PatchDiagnosticCount { get; private set; }
    private static bool recordingPatchDiagnostics;

    public static void BeginPatchAudit()
    {
        PatchDiagnosticCount = 0;
        recordingPatchDiagnostics = true;
    }

    public static void EndPatchAudit() => recordingPatchDiagnostics = false;

    public static void Init(ILogger logger)
    {
        Log.logger = logger;
    }

    public static void Debug(string message)
    {
        logger?.LogDebug(message);
    }

    public static void Debug(object message)
    {
        Debug(message?.ToString());
    }

    public static void Info(string message)
    {
        logger?.LogInfo(message);
    }

    public static void Warn(string message)
    {
        if (recordingPatchDiagnostics) PatchDiagnosticCount++;
        logger?.LogWarning(message);
    }

    public static void Warn(object message)
    {
        Warn(message?.ToString());
    }

    public static void Error(string message)
    {
        ErrorWithoutPopup(message);
        if (UIFatalErrorTip.instance != null)
        {
            // Test if current code is executing on the main unity thread
            if (BepInEx.ThreadingHelper.Instance.InvokeRequired)
            {
                // ShowError has Unity API and needs to call on the main thread
                BepInEx.ThreadingHelper.Instance.StartSyncInvoke(() =>
                {
                    // We just want to use the window to show the error message, so leave GameMain.errored as the original value
                    var tmp = GameMain.errored;
                    UIFatalErrorTip.instance.ShowError("[Nebulae Error] " + message, "");
                    GameMain.errored = tmp;
                });
                return;
            }
            UIFatalErrorTip.instance.ShowError("[Nebulae Error] " + message, "");
        }
    }

    public static void Error(Exception ex)
    {
        Error(ex?.ToString());
    }

    public static void Error(string message, Exception ex)
    {
        Error(message + (ex == null ? "" : "\n" + ex));
    }

    // Background failures must not open Unity UI or produce a popup for every failed tick.
    public static void ErrorWithoutPopup(string message, Exception ex = null)
    {
        if (recordingPatchDiagnostics) PatchDiagnosticCount++;
        var detail = message + (ex == null ? "" : "\n" + ex);
        logger?.LogError(detail);
        LastErrorMsg = detail;
    }

    public static void SocketOutput(LogData data, string _)
    {
        var log = data.ToString();
        const string Ipv4Regex = @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}";
        const string Ipv6Regex = "([0-9a-fA-F]{1,4}:){7,7}[0-9a-fA-F]{1,4}";
        log = Regex.Replace(log, Ipv4Regex, "(IPv4 Address)");
        log = Regex.Replace(log, Ipv6Regex, "(IPv6 Address)");

        if (data.Level >= LogLevel.Error)
        {
            Diagnostics.Error("socket:" + data.Level + ":" + data.Message, () => log);
        }
        else if (data.Level == LogLevel.Warn)
        {
            Diagnostics.Warn("socket:" + data.Message, () => log);
        }
        else Debug(log);
    }
}
