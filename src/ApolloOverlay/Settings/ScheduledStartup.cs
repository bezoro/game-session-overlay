using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ApolloOverlay.Automation;

namespace ApolloOverlay;

internal sealed record ScheduledStartupStatus(bool Exists, bool Owned, bool Enabled, bool Elevated);

/// <summary>Manages only this installation's hidden task for the current interactive user.</summary>
internal static class ScheduledStartup
{
    internal static bool IsMissingTask(Exception error) => error.HResult == unchecked((int)0x80070002)
        && error is COMException or FileNotFoundException;

    private sealed class ComObjects : IDisposable
    {
        private readonly List<object> objects = [];
        internal dynamic Keep(object value)
        {
            objects.Add(value);
            return value;
        }

        public void Dispose()
        {
            foreach (var value in objects.AsEnumerable().Reverse())
                if (Marshal.IsComObject(value))
                    Marshal.ReleaseComObject(value);
        }
    }

    private static T WithScheduler<T>(Func<dynamic, dynamic, ComObjects, string, T> operation)
    {
        using var objects = new ComObjects();
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is unavailable.");
        dynamic service = objects.Keep(Activator.CreateInstance(type) ?? throw new InvalidOperationException("Task Scheduler is unavailable."));
        service.Connect();
        dynamic folder = objects.Keep(service.GetFolder("\\"));
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user could not be identified.");
        return operation(service, folder, objects, sid);
    }

    private static string Name(string sid) => "Bezoro Game Session Overlay - " + sid;

    private static dynamic? FindTask(dynamic folder, ComObjects objects, string sid)
    {
        try
        {
            return objects.Keep(folder.GetTask(Name(sid)));
        }
        catch (Exception error) when (IsMissingTask(error))
        {
            return null;
        }
    }

    internal static bool PrincipalMatches(string principal, string sid)
    {
        if (string.Equals(principal, sid, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(principal) || principal.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return new NTAccount(principal).Translate(typeof(SecurityIdentifier)).Value == sid;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }

    private static bool Owned(dynamic definition, string executable, ComObjects objects, string sid)
    {
        dynamic actions = objects.Keep(definition.Actions);
        if ((int)actions.Count != 1)
            return false;
        dynamic action = objects.Keep(actions.Item(1));
        if ((int)action.Type != 0)
            return false;
        dynamic principal = objects.Keep(definition.Principal);
        return StartMenuRegistration.MatchesInstallation((string)action.Path, executable)
            && ((string)action.Arguments).Trim() is "--startup" or "--hidden"
            && (int)principal.LogonType == 3
            && PrincipalMatches((string)principal.UserId, sid);
    }

    internal static ScheduledStartupStatus Read(string executable) => WithScheduler((_, folder, objects, sid) =>
    {
        dynamic? task = FindTask(folder, objects, sid);
        if (task is null)
            return new ScheduledStartupStatus(false, false, false, false);
        dynamic definition = objects.Keep(task.Definition);
        var owned = Owned(definition, executable, objects, sid);
        dynamic principal = objects.Keep(definition.Principal);
        return new ScheduledStartupStatus(true, owned, (bool)task.Enabled, (int)principal.RunLevel == 1);
    });

    internal static bool SetEnabled(string executable, bool enabled) => WithScheduler((_, folder, objects, sid) =>
    {
        dynamic? task = FindTask(folder, objects, sid);
        if (task is null)
            return false;
        if (!Owned(objects.Keep(task.Definition), executable, objects, sid))
            return false;
        task.Enabled = enabled;
        return (bool)task.Enabled == enabled;
    });

    /// <summary>Runs a previously consented task in this logon session, without displaying UAC.</summary>
    internal static bool TryRunElevated(string executable)
    {
        if (!OwnInstalledLauncher(executable) || AutomationPipeSecurity.Elevated())
            return false;
        try
        {
            return WithScheduler((_, folder, objects, sid) =>
            {
                dynamic? task = FindTask(folder, objects, sid);
                if (task is null || !(bool)task.Enabled)
                    return false;
                dynamic definition = objects.Keep(task.Definition);
                if (!Owned(definition, executable, objects, sid))
                    return false;
                dynamic principal = objects.Keep(definition.Principal);
                if ((int)principal.RunLevel != 1)
                    return false;
                // TASK_RUN_USE_SESSION_ID prevents a demand launch in another interactive session.
                objects.Keep(task.RunEx(null, 4, AutomationPipeSecurity.Current().Session, null));
                return true;
            });
        }
        catch (Exception error) when (StartupRegistration.RegistrationError(error))
        {
            return false;
        }
    }

    private static bool OwnInstalledLauncher(string executable) => VelopackInstallation.IsInstalled
        && string.Equals(executable, VelopackInstallation.StartupExecutable, StringComparison.OrdinalIgnoreCase)
        && File.Exists(executable);

    /// <summary>Changes task registration transactionally; registry changes run inside the rollback boundary.</summary>
    internal static void ConfigureElevation(string executable, bool elevated, bool enabled, Action commit)
    {
        if (!OwnInstalledLauncher(executable) || !AutomationPipeSecurity.Elevated())
            throw new UnauthorizedAccessException("Administrator consent is required for this installed application's task.");
        WithScheduler((service, folder, objects, sid) =>
        {
            dynamic? previous = FindTask(folder, objects, sid);
            if (previous is not null && !Owned(objects.Keep(previous.Definition), executable, objects, sid))
                throw new InvalidOperationException("The existing task belongs to another installation or user.");
            string? xml = previous is null ? null : (string)previous.Xml;
            string? security = previous is null ? null : (string)previous.GetSecurityDescriptor(7);
            var changed = false;
            try
            {
                if (elevated)
                {
                    dynamic definition = objects.Keep(service.NewTask(0));
                    dynamic registration = objects.Keep(definition.RegistrationInfo);
                    registration.Description = "Starts Game Session Overlay hidden for the current interactive user.";
                    dynamic principal = objects.Keep(definition.Principal);
                    principal.UserId = sid;
                    principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN; no password or alternate credentials.
                    principal.RunLevel = 1;
                    dynamic settings = objects.Keep(definition.Settings);
                    settings.Enabled = enabled;
                    settings.AllowDemandStart = true;
                    settings.DisallowStartIfOnBatteries = false;
                    settings.StopIfGoingOnBatteries = false;
                    settings.StartWhenAvailable = true;
                    settings.ExecutionTimeLimit = "PT0S";
                    settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW.
                    settings.Hidden = true;
                    dynamic triggers = objects.Keep(definition.Triggers);
                    dynamic trigger = objects.Keep(triggers.Create(9));
                    trigger.UserId = sid;
                    trigger.Enabled = true;
                    dynamic actions = objects.Keep(definition.Actions);
                    dynamic action = objects.Keep(actions.Create(0));
                    action.Path = executable;
                    action.Arguments = "--startup";
                    action.WorkingDirectory = Path.GetDirectoryName(executable)!;
                    // The same user may read/run the configured task. Updating it requires elevation.
                    var permissions = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{sid})";
                    dynamic registered = objects.Keep(folder.RegisterTaskDefinition(Name(sid), definition, previous is null ? 2 : 4, sid, null, 3, permissions));
                    changed = true;
                    if ((bool)registered.Enabled != enabled || !Owned(objects.Keep(registered.Definition), executable, objects, sid))
                        throw new InvalidOperationException("Task Scheduler did not confirm the requested registration.");
                }
                else if (previous is not null)
                {
                    folder.DeleteTask(Name(sid), 0);
                    changed = true;
                }
                commit();
                return true;
            }
            catch
            {
                if (changed)
                {
                    if (xml is null)
                        folder.DeleteTask(Name(sid), 0);
                    else
                        objects.Keep(folder.RegisterTask(Name(sid), xml, 6, sid, null, 3, security));
                }
                throw;
            }
        });
    }

    internal static void RemoveMatching(string executable)
    {
        WithScheduler((_, folder, objects, sid) =>
        {
            dynamic? task = FindTask(folder, objects, sid);
            if (task is not null && Owned(objects.Keep(task.Definition), executable, objects, sid))
                folder.DeleteTask(Name(sid), 0);
            return false;
        });
    }
}
