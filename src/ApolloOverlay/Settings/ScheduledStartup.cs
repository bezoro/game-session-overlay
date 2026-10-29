using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ApolloOverlay;

internal sealed record ScheduledStartupStatus(bool Exists, bool Owned, bool Enabled, bool Elevated);

/// <summary>Reads or updates only this user's explicitly configured startup task.</summary>
internal static class ScheduledStartup
{
    internal static bool IsMissingTask(Exception error) => error.HResult == unchecked((int)0x80070002)
        && error is COMException or FileNotFoundException;

    private static T WithTask<T>(Func<dynamic, dynamic, dynamic, dynamic, T> found, Func<T> missing)
    {
        var objects = new List<object>();
        object Keep(object value)
        {
            objects.Add(value);
            return value;
        }
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is unavailable.");
            dynamic service = Keep(Activator.CreateInstance(type) ?? throw new InvalidOperationException("Task Scheduler is unavailable."));
            service.Connect();
            dynamic folder = Keep(service.GetFolder("\\"));
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user could not be identified.");
            dynamic task;
            try
            {
                task = Keep(folder.GetTask("Bezoro Game Session Overlay - " + sid));
            }
            catch (Exception error) when (IsMissingTask(error))
            {
                return missing();
            }
            dynamic definition = Keep(task.Definition);
            dynamic actions = Keep(definition.Actions);
            if ((int)actions.Count != 1)
                return found(task, null!, null!, folder);
            dynamic action = Keep(actions.Item(1));
            dynamic principal = Keep(definition.Principal);
            return found(task, action, principal, folder);
        }
        finally
        {
            foreach (var value in objects.AsEnumerable().Reverse())
                if (Marshal.IsComObject(value))
                    Marshal.FinalReleaseComObject(value);
        }
    }

    private static bool Owned(dynamic action, string executable) => action is not null
        && StartMenuRegistration.MatchesInstallation((string)action.Path, executable)
        && ((string)action.Arguments).Trim() is "--startup" or "--hidden";

    internal static ScheduledStartupStatus Read(string executable) => WithTask((task, action, principal, _) => new ScheduledStartupStatus(true, Owned(action, executable), (bool)task.Enabled, principal is not null && (int)principal.RunLevel == 1), () => new(false, false, false, false));

    internal static bool SetEnabled(string executable, bool enabled) => WithTask((task, action, _, _) =>
    {
        if (!Owned(action, executable))
            return false;
        task.Enabled = enabled;
        return (bool)task.Enabled == enabled;
    }, () => false);

    internal static void RemoveMatching(string executable)
    {
        WithTask((task, action, _, folder) =>
        {
            if (Owned(action, executable))
                folder.DeleteTask((string)task.Name, 0);
            return false;
        }, () => false);
    }
}
