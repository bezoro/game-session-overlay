using System.ComponentModel;
using System.Text.Json;
using ApolloOverlay.Automation;

internal static class UpdateCommands
{
    private sealed record Options(string Command, string Pipe, string Channel, string BuildId, string JobId, bool DryRun, int TimeoutSeconds);
    internal static async Task<int> Run(string[] args)
    {
        if (args.Any(arg => arg is "--help" or "-h"))
        {
            Console.WriteLine("gso version | history | update [install|check|status|cancel JOB_ID] [--build BUILD_ID] [--channel testing|stable] [--dry-run] [--timeout 5..600] | rollback BUILD_ID. JSON output. update requests a restart; confirm the version with a fresh gso version invocation. No implicit launch or elevation.");
            return 0;
        }

        return Program.Print(await Execute(args, Program.Send, CancellationToken.None));
    }

    internal static async Task<AutomationReply> Execute(
        string[] args,
        Func<string, AutomationRequest, CancellationToken, Task<AutomationReply>> send,
        CancellationToken cancellation,
        string? defaultPipe = null,
        Func<CancellationToken, Task>? pause = null
    )
    {
        string job = "";
        string endpoint = "";
        var applying = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            deadline.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var options = Parse(args, defaultPipe);
            endpoint = options.Pipe;
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var token = deadline.Token;
            Task<AutomationReply> Send(AutomationRequest request)
            {
                token.ThrowIfCancellationRequested();
                return send(endpoint, request, token);
            }

            async Task<AutomationReply> Wait(AutomationReply started, string expectedPhase, string instance)
            {
                if (started.Outcome != "Requested")
                    return started;
                if (started.Update is not { JobId.Length: > 0 } state)
                    return AutomationReply.Error("Unknown", "The server did not identify the started operation. Check update status before continuing.");
                job = state.JobId;
                while (true)
                {
                    if (pause is null)
                        await Task.Delay(250, token);
                    else
                        await pause(token);
                    var reply = await Send(new()
                    {
                        Verb = "update-status"
                    });
                    if (reply.Outcome != "Completed" || reply.Update is null)
                        return reply;
                    if (instance.Length > 0 && reply.Instance != instance)
                        return AutomationReply.Error("Unknown", "The instance changed during preparation. Check the version; no new request will be sent.", job) with
                        {
                            Update = reply.Update
                        };
                    if (reply.Update.JobId != job)
                        return AutomationReply.Error("Unknown", "Another operation replaced the request. No installation will be requested.", job) with
                        {
                            Update = reply.Update
                        };
                    if (!reply.Update.Terminal)
                        continue;
                    if (reply.Update.Phase == expectedPhase)
                        return reply;
                    return reply with
                    {
                        Outcome = reply.Update.Phase == "Cancelled" ? "Cancelled" : "Failed",
                        Reason = reply.Update.Message
                    };
                }
            }

            if (options.Command == "cancel")
                return await Send(new()
                {
                    Verb = "update-cancel",
                    JobId = options.JobId,
                    DryRun = options.DryRun
                });
            var current = await Send(new()
            {
                Verb = "update-status"
            });
            if (current.Outcome == "Snapshot" && options.Command is not ("version" or "status" or "history"))
                return current with
                {
                    Outcome = "Rejected",
                    Reason = "A stale observation does not authorize an update. Connect to the running instance."
                };
            if (current.Outcome != "Completed" || current.Update is null)
                return current.Outcome == "Completed" ? AutomationReply.Error("Unsupported", "The instance does not provide the update API. Install the latest Setup.") : current;
            if (options.Command is "version" or "status" or "history")
                return current;
            if (options.Command != "check" && (!current.Update.Installed || current.Update.SettingsDirty))
                return current with
                {
                    Outcome = "Rejected",
                    Reason = current.Update.SettingsDirty ? "Save or discard settings before switching versions." : "Install the initial version using Setup before updating through the CLI."
                };
            var channel = options.Channel.Length > 0 ? options.Channel : current.Update.SelectedChannel;
            AutomationReply ready;
            if (options.Command == "rollback")
            {
                var request = new AutomationRequest
                {
                    Verb = "update-rollback",
                    BuildId = options.BuildId,
                    DryRun = options.DryRun
                };
                ready = await Send(request);
                if (options.DryRun)
                    return ready;
                ready = await Wait(ready, "Ready", current.Instance);
            }
            else
            {
                ready = await Send(new()
                {
                    Verb = "update-check",
                    Channel = channel,
                    DryRun = options.DryRun
                });
                if (options.DryRun)
                    return ready;
                ready = await Wait(ready, "Catalog", current.Instance);
                if (ready.Outcome != "Completed" || ready.Update is null || options.Command == "check")
                    return ready;
                var selected = options.BuildId.Length == 0 ? ready.Update.Available.FirstOrDefault() : ready.Update.Available.FirstOrDefault(release => release.BuildId == options.BuildId);
                if (selected is null)
                    return ready with
                    {
                        Outcome = options.BuildId.Length == 0 ? "Unavailable" : "Rejected",
                        Reason = "No matching release was found in the selected channel."
                    };
                if (selected.BuildId == ready.Update.BuildId)
                    return ready with
                    {
                        Reason = "The selected version is already running."
                    };
                ready = await Send(new()
                {
                    Verb = "update-download",
                    BuildId = selected.BuildId,
                    Channel = channel,
                    AllowDowngrade = options.BuildId.Length > 0
                });
                ready = await Wait(ready, "Ready", current.Instance);
            }

            if (ready.Outcome != "Completed" || ready.Update is null)
                return ready;
            if (ready.Update.SettingsDirty)
                return ready with
                {
                    Outcome = "Rejected",
                    Reason = "Settings changed during download. Save or discard them before applying the update."
                };
            if (ready.Update.TargetBuildId.Length == 0)
                return ready with
                {
                    Outcome = "Rejected",
                    Reason = "The prepared package does not identify the target version."
                };
            applying = true;
            return await Send(new()
            {
                Verb = "update-apply",
                BuildId = ready.Update.TargetBuildId,
                JobId = ready.Update.JobId
            });
        }
        catch (OperationCanceledException)
        {
            if (!applying && job.Length > 0 && endpoint.Length > 0)
            {
                using var stop = new CancellationTokenSource(3000);
                try
                {
                    await send(endpoint, new()
                    {
                        Verb = "update-cancel",
                        JobId = job
                    }, stop.Token);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or OperationCanceledException or TimeoutException)
                {
                }
            }

            return AutomationReply.Error("Unknown", applying ? "Confirmation interrupted after requesting application. Check gso version and update status; do not retry automatically." : "Preparation timed out or was cancelled. No application was requested; check update status.", job);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or Win32Exception or TimeoutException)
        {
            return AutomationReply.Error(applying ? "Unknown" : "Unavailable", "The local instance did not respond. Check that it is running under the same user, session and elevation level. No launch or retry was attempted.", job);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            return AutomationReply.Error("Rejected", error.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static Options Parse(string[] args, string? defaultPipe)
    {
        if (args.Length == 0)
            throw new ArgumentException("Specify a command.");
        var command = args[0];
        var index = 1;
        var build = "";
        var job = "";
        if (command == "update")
        {
            command = "install";
            if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
                command = args[index++];
            if (command == "cancel")
            {
                if (index >= args.Length)
                    throw new ArgumentException("Specify the JobId.");
                job = args[index++];
            }
        }
        else if (command == "rollback")
        {
            if (index >= args.Length)
                throw new ArgumentException("Specify the retained BuildId.");
            build = args[index++];
        }

        if (command is not ("version" or "history" or "status" or "install" or "check" or "cancel" or "rollback"))
            throw new ArgumentException("Unknown update command.");
        var pipe = defaultPipe ?? "";
        var channel = "";
        var dry = false;
        var seconds = 180;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option))
                throw new ArgumentException("Repeated option: " + option);
            if (option == "--dry-run")
            {
                dry = true;
                continue;
            }

            if (++index >= args.Length)
                throw new ArgumentException("Missing value for " + option);
            switch (option)
            {
                case "--pipe":
                    pipe = args[index];
                    break;
                case "--channel":
                    channel = args[index];
                    break;
                case "--build" when command == "install":
                    build = args[index];
                    break;
                case "--timeout":
                    seconds = int.Parse(args[index], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException("Unsupported option: " + option);
            }
        }

        if (channel.Length > 0 && (command is not ("install" or "check") || channel is not ("testing" or "stable")))
            throw new ArgumentException("Invalid channel for this command.");
        if (seconds is < 5 or > 600)
            throw new ArgumentException("The timeout must be between 5 and 600 seconds.");
        if (seen.Contains("--build")
            && build.Length == 0
            || build.Length > 0
            && (build.Length > 96
            || build.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character is not ('.' or '-' or '_'))))
            throw new ArgumentException("Invalid BuildId.");
        if (command == "rollback" && build.Length == 0 || command == "cancel" && (job.Length == 0 || job.Length > 256 || job.Any(char.IsControl)))
            throw new ArgumentException("Missing or invalid identifier.");
        if (dry && command is "version" or "history" or "status")
            throw new ArgumentException("Status queries are already read-only.");
        if (pipe.Length == 0)
            pipe = AutomationPipeSecurity.DefaultName;
        if (pipe.Length > 128 || pipe.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ArgumentException("Only local endpoints are accepted.");
        return new(command, pipe, channel, build, job, dry, seconds);
    }
}
