using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace NebulaeModel.Logger;

// Explicit wire names also keep queued reports readable across release builds.
[DataContract]
internal sealed class ErrorReport
{
    [DataMember(Name = "schemaVersion")] public int SchemaVersion = 1;
    [DataMember(Name = "reportId")] public string ReportId = Guid.NewGuid().ToString("N");
    [DataMember(Name = "sessionId")] public string SessionId;
    [DataMember(Name = "timestamp")] public string Timestamp;
    [DataMember(Name = "nebulaeVersion")] public string NebulaeVersion;
    [DataMember(Name = "gameVersion")] public string GameVersion;
    [DataMember(Name = "runtime")] public string Runtime;
    [DataMember(Name = "role")] public string Role;
    [DataMember(Name = "mods")] public string[] Mods;
    [DataMember(Name = "errors")] public ReportError[] Errors;
    [DataMember(Name = "context")] public string Context;
    [DataMember(Name = "truncated")] public bool Truncated;
    [DataMember(Name = "droppedEvents")] public long DroppedEvents;

    internal static string Time(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

[DataContract]
internal sealed class ReportError
{
    [DataMember(Name = "source")] public string Source;
    [DataMember(Name = "role")] public string Role;
    [DataMember(Name = "message")] public string Message;
    [DataMember(Name = "stack")] public string Stack;
    [DataMember(Name = "firstAt")] public string FirstAt;
    [DataMember(Name = "lastAt")] public string LastAt;
    [DataMember(Name = "count")] public long Count;
}

internal sealed class ReportEnvironment
{
    public string NebulaeVersion = "unknown", GameVersion = "unknown", Runtime = "unknown", Role = "startup";
    public string[] Mods = Array.Empty<string>();
    public string[] Secrets = Array.Empty<string>();
}

internal static class ReportCodec
{
    internal const int MaximumBytes = 256 * 1024;
    internal static byte[] Encode(ErrorReport report)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(ErrorReport)).WriteObject(stream, report);
        return stream.ToArray();
    }

    internal static ErrorReport Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Oversized queued report");
        using var stream = new MemoryStream(bytes);
        return (ErrorReport)new DataContractJsonSerializer(typeof(ErrorReport)).ReadObject(stream);
    }

    // Encoding can expand control characters sixfold. Bound the actual wire bytes,
    // not just string lengths, and never split a UTF-16 surrogate pair.
    internal static byte[] EncodeBounded(ErrorReport report)
    {
        while (true)
        {
            var bytes = Encode(report);
            if (bytes.Length <= MaximumBytes) return bytes;
            report.Truncated = true;
            if (report.Context.Length > 1024) report.Context = Clip(report.Context, report.Context.Length / 2);
            else
            {
                var largest = report.Errors.OrderByDescending(e => e.Message.Length + e.Stack.Length).First();
                if (largest.Stack.Length > 128) largest.Stack = Clip(largest.Stack, largest.Stack.Length / 2);
                else if (largest.Message.Length > 128) largest.Message = Clip(largest.Message, largest.Message.Length / 2);
                else if (report.Mods.Length > 0) report.Mods = report.Mods.Take(report.Mods.Length / 2).ToArray();
                else throw new InvalidDataException("Report metadata exceeds wire budget");
            }
        }
    }

    internal static string Clip(string value, int characters)
    {
        value ??= "";
        if (value.Length <= characters) return value;
        if (characters > 0 && char.IsHighSurrogate(value[characters - 1])) characters--;
        return value.Substring(0, characters);
    }

    internal static string ClipUtf8(string value, int bytes)
    {
        value ??= "";
        if (Encoding.UTF8.GetByteCount(value) <= bytes) return value;
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(value.Substring(0, middle)) <= bytes) low = middle;
            else high = middle - 1;
        }
        return Clip(value, low);
    }
}

internal sealed class ReportRedactor
{
    public bool Truncated { get; private set; }
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex credentials = new(
        @"(?i)([""']?\b(?:password|passwd|pwd|token|access_token|refresh_token|api[_-]?key|secret|authorization|cookie|ticket)\b[""']?\s*[:=]\s*)(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|(?:Bearer|Basic)\s+[^\s,;]+|[^\s,;}\]]+)",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex headers = new(@"(?im)(\b(?:Authorization|Cookie)\s*:\s*)[^\r\n]+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex userInfo = new(@"(?i)((?:https?|wss?)://)[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex authorization = new(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9+/_.=:-]+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex userDirectory = new(@"(?i)([a-z]:[\\/]Users[\\/])[^\\/\s:]+|(/(?:home|Users)/)[^/\s:]+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex addresses = new(@"(?<![\w:.])(?=[a-fA-F0-9:.%]*[.:])[a-fA-F0-9:.%]{3,}(?![\w:.])", RegexOptions.CultureInvariant, RegexTimeout);
    private readonly string[] secrets;

    public ReportRedactor(IEnumerable<string> secrets)
    {
        this.secrets = (secrets ?? Array.Empty<string>()).Where(s => !string.IsNullOrEmpty(s))
            .Distinct().OrderByDescending(s => s.Length).Take(256).ToArray();
    }

    public string Clean(string text, int maxBytes)
    {
        // Fail closed: a pathological regex input must never get uploaded unsanitized.
        try
        {
            if (text?.Length > 65536) Truncated = true;
            text = ReportCodec.Clip(text, 65536);
            foreach (var secret in secrets) text = text.Replace(secret, "[redacted]");
            text = headers.Replace(text, "$1[redacted]");
            text = credentials.Replace(text, "$1[redacted]");
            text = authorization.Replace(text, "$1 [redacted]");
            text = userInfo.Replace(text, "$1[redacted]@");
            text = userDirectory.Replace(text, match => (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) + "[user]");
            text = addresses.Replace(text, match =>
            {
                if (IPAddress.TryParse(match.Value, out _)) return "[IP]";
                var port = match.Value.LastIndexOf(':');
                if (port > 0 && match.Value.Substring(0, port).Contains(".") && IPAddress.TryParse(match.Value.Substring(0, port), out _))
                    return "[IP]" + match.Value.Substring(port);
                return match.Value;
            });
            text = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text.Replace("\0", "[NUL]")));
            if (Encoding.UTF8.GetByteCount(text) > maxBytes) Truncated = true;
            return ReportCodec.ClipUtf8(text, maxBytes);
        }
        catch (RegexMatchTimeoutException) { Truncated = true; return "[report text removed: sanitization timeout]"; }
    }
}
