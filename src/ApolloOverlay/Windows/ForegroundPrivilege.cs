using System.ComponentModel;
using System.Security;
using ApolloOverlay.Automation;

namespace ApolloOverlay;

/// <summary>Observes privilege only after a refused activation, without inferring it from a process name.</summary>
internal sealed record ForegroundPrivilege(string Message, string Diagnostic)
{
    internal const string Unconfirmed = "Windows did not confirm focus. Try opening from the tray.";

    internal static ForegroundPrivilege Inspect(nint foreground)
    {
        var thread = Native.GetWindowThreadProcessId(foreground, out var pid);
        try
        {
            var current = AutomationPipeSecurity.Current();
            var other = AutomationPipeSecurity.ReadPeer(checked((int)pid));
            if (Native.GetForegroundWindow() != foreground)
                return new(Unconfirmed, "foreground-changed");
            var higher = !current.Elevated && other.Elevated && AutomationPipeSecurity.SameSession(current, other);
            return new(higher
                ? "Windows did not confirm focus. The foreground app runs as administrator. Restart the overlay as administrator from the tray."
                : Unconfirmed,
                $"pid={pid}/thread={thread}/self-elevated={current.Elevated}/foreground-elevated={other.Elevated}/same-session={AutomationPipeSecurity.SameSession(current, other)}");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException or OverflowException or SecurityException)
        {
            return new(Unconfirmed, $"pid={pid}/thread={thread}/privilege-unavailable={error.GetType().Name}");
        }
    }
}
