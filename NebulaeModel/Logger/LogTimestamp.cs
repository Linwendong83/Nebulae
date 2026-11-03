#region

using System;
using System.Globalization;
using System.Text.RegularExpressions;

#endregion

namespace NebulaeModel.Logger;

/// <summary>
///     Wall-clock stamp on every log line this mod writes through BepInEx.
/// </summary>
/// <remarks>
///     A BepInEx log line carries the level and the plugin name, never the time, so a host log and a
///     client report could only be aligned by guessing. Both sides now write the same
///     <c>[HH:mm:ss.fff]</c> prefix, which is what makes "the client stalled at 20:32:50" provable
///     from the host file alone.
/// </remarks>
internal static class LogTimestamp
{
    internal const string Pattern = "HH:mm:ss.fff";
    private static readonly Regex Prefix = new(@"^[d{2}:d{2}:d{2}.d{3}] ", RegexOptions.Compiled);

    public static string Stamp(object data) =>
        "[" + DateTime.Now.ToString(Pattern, CultureInfo.InvariantCulture) + "] " + (data ?? string.Empty);

    /// <summary>Removes the stamp again for consumers that write their own timestamp (the report ring).</summary>
    public static string Strip(string message) =>
        string.IsNullOrEmpty(message) || !Prefix.IsMatch(message) ? message : Prefix.Replace(message, string.Empty, 1);
}
