using NebulaeModel.Logger;

namespace NebulaeModel.Authority;

internal static class BindingDiagnostics
{
    public static void Refused(string reason, ObjectKey key, int bytes)
    {
        Diagnostics.Warn($"binding:{reason}:{key.Kind}:{key.Scope}",
            () => $"[authority] binding refused reason={reason} key={key} bytes={bytes}");
    }

    public static void OutsideWindow(string binding, ScopeKey scope)
    {
        Diagnostics.Error($"binding:window:{binding}:{scope}",
            () => $"[authority] binding refused outside apply window binding={binding} scope={scope}");
    }
}
