using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using ApolloOverlay.Automation;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "logs")
            return LogCommands.Run(args);
        if (args.Length > 0 && args[0] is "version" or "update" or "history" or "rollback")
            return await UpdateCommands.Run(args);
        if (args.Length == 0 || args[0] is "--help" or "help")
        {
            Console.WriteLine("gso logs [--tail 1..200] reads local command logs even while the overlay is closed. gso version|history|update [install|check|status|cancel JOB_ID] [--build BUILD_ID] [--channel testing|stable] [--dry-run] [--timeout 5..600]|rollback BUILD_ID. gso status|targets|actions|trace|result ID|execute ACTION [--target ID] [--intent activate|left|right] [--revision N] [--dry-run]|cancel ID|shutdown [--bridge] [--pipe LOCAL_NAME]. JSON output; no implicit launch, elevation or retry. Confirm the version after updating with a fresh gso version invocation.");
            return 0;
        }

        using var cancellation = new CancellationTokenSource(10000);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        AutomationRequest request = new()
        {
            Verb = args[0],
            DeadlineUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000
        };
        var pipe = AutomationPipeSecurity.DefaultName;
        var bridge = false;
        try
        {
            var index = 1;
            if (request.Verb is "execute" or "result" or "cancel")
            {
                if (args.Length < 2)
                    throw new ArgumentException("Expected action/ID.");
                request = request.Verb == "execute" ? request with
                {
                    ActionId = args[1]
                }

                : request with
                {
                    RequestId = args[1]
                };
                index = 2;
            }

            for (; index < args.Length; index++)
                switch (args[index])
                {
                    case "--target":
                        request = request with
                        {
                            TargetId = args[++index]
                        };
                        break;
                    case "--intent":
                        request = request with
                        {
                            Intent = args[++index]
                        };
                        break;
                    case "--revision":
                        request = request with
                        {
                            Revision = long.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture)
                        };
                        break;
                    case "--preset":
                        request = request with
                        {
                            Preset = args[++index]
                        };
                        break;
                    case "--dry-run":
                        request = request with
                        {
                            DryRun = true
                        };
                        break;
                    case "--bridge":
                        bridge = true;
                        break;
                    case "--pipe":
                        pipe = args[++index];
                        break;
                    default:
                        throw new ArgumentException("Unsupported option.");
                }

            if (pipe.Length > 128 || pipe.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
                throw new ArgumentException("Only a local endpoint name is accepted.");
            if (bridge)
                pipe += ".bridge";
            if (request.Verb == "execute" && request.Revision < 0)
            {
                var state = await Send(pipe, new()
                {
                    Verb = "status"
                }, cancellation.Token);
                if (state.Snapshot is null)
                    return Print(state);
                request = request with
                {
                    Revision = state.Snapshot.Revision
                };
            }

            return Print(await Send(pipe, request, cancellation.Token));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or Win32Exception)
        {
            if (request.Verb is "status" or "targets" or "actions" or "trace" or "result")
                return Print(ReadOnly(request));
            return Print(AutomationReply.Error("Unavailable", "Instance or authority unavailable; no implicit launch or retry."));
        }
        catch (OperationCanceledException)
        {
            return Print(AutomationReply.Error("Unknown", "Timed out or cancelled; check result/trace before another action."));
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException or OverflowException or JsonException)
        {
            return Print(AutomationReply.Error("Rejected", e.Message));
        }
    }

    internal static int Print(AutomationReply reply)
    {
        Console.WriteLine(JsonSerializer.Serialize(reply, AutomationJson.Default.AutomationReply));
        return reply.Outcome is "Completed" or "Requested" or "Simulated" or "DryRun" or "Snapshot" ? 0 : reply.Outcome == "Unknown" ? 3 : 1;
    }

    internal static async Task<AutomationReply> Send(string name, AutomationRequest request, CancellationToken token)
    {
        using var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(1000, token);
        var handshake = JsonSerializer.Deserialize(await AutomationFrames.Read(stream, AutomationFrames.ResponseLimit, token), AutomationJson.Default.AutomationReply) ?? throw new InvalidDataException();
        if (handshake.Outcome != "Handshake" || !AutomationPipeSecurity.ServerAllowed(stream, handshake, name.EndsWith(".bridge", StringComparison.Ordinal)))
            return AutomationReply.Error("Denied", "Server identity or elevation unconfirmed.");
        using var server = Process.GetProcessById(handshake.Pid);
        if (server.StartTime.ToUniversalTime().Ticks != handshake.Started)
            return AutomationReply.Error("Denied", "The instance changed.");
        if (request.Verb is "execute" or "shutdown" or "update-check" or "update-download" or "update-rollback" or "update-cancel" or "update-apply")
            request = request with
            {
                RequestId = handshake.Ticket,
                DeadlineUtc = Math.Min(request.DeadlineUtc > 0 ? request.DeadlineUtc : handshake.ExpiresUtc, handshake.ExpiresUtc)
            };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (request.Verb is "shutdown" or "update-apply")
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, request.DeadlineUtc - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        token = deadline.Token;
        ShutdownProcess? observation = null;
        try
        {
            if (request.Verb is "shutdown" or "update-apply")
                observation = ShutdownProcess.Open(handshake.Pid, handshake.Started);
        }
        catch (Exception e) when (e is Win32Exception or InvalidDataException)
        {
            return AutomationReply.Error("Denied", "Could not retain an observation of the instance; shutdown was not sent.", request.RequestId);
        }

        using var observed = observation;
        await AutomationFrames.Write(stream, AutomationFrames.Encode(request), AutomationFrames.RequestLimit, token);
        AutomationReply response;
        try
        {
            response = JsonSerializer.Deserialize(await AutomationFrames.Read(stream, AutomationFrames.ResponseLimit, token), AutomationJson.Default.AutomationReply) ?? throw new InvalidDataException();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            return AutomationReply.Error("Unknown", "Response unconfirmed; do not retry automatically.", request.RequestId);
        }

        if (request.Verb == "shutdown" && response.Outcome == "ShutdownRequested")
        {
            return await ObserveShutdown(response, observed!.WaitForExit, token);
        }

        if (request.Verb == "update-apply" && response.Outcome == "UpdateApplyRequested")
        {
            var exited = await ObserveShutdown(response, observed!.WaitForExit, token);
            return exited with
            {
                Outcome = exited.Outcome == "Completed" ? "Requested" : "Unknown",
                Terminal = false,
                Reason = exited.Outcome == "Completed" ? "The previous instance exited. Update requested through Velopack; confirm the new version with gso version after the restart." : "Exit unconfirmed. Check gso update status before making another request."
            };
        }

        return response;
    }

    internal static async Task<AutomationReply> ObserveShutdown(AutomationReply response, Func<CancellationToken, Task> wait, CancellationToken token)
    {
        try
        {
            await wait(token);
            return response with
            {
                Outcome = "Completed",
                Terminal = true,
                Reason = "The instance with the exact PID and creation identity exited normally."
            };
        }
        catch (Exception e) when (e is OperationCanceledException or Win32Exception)
        {
            return response with
            {
                Outcome = "Unknown",
                Terminal = true,
                Reason = "Instance exit unconfirmed; no force-kill or retry will be attempted."
            };
        }
    }

    private static AutomationReply ReadOnly(AutomationRequest request)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay", "automation", Process.GetCurrentProcess().SessionId.ToString());
        try
        {
            if (request.Verb == "result")
            {
                var path = Path.Combine(root, "automation-ledger.json");
                if (new FileInfo(path).Length > 1024 * 1024)
                    throw new InvalidDataException();
                var records = JsonSerializer.Deserialize(File.ReadAllBytes(path), AutomationJson.Default.AutomationRecords);
                return records?.Records.FirstOrDefault(r => r.Id == request.RequestId)?.Reply ?? AutomationReply.Error("Unavailable", "ID not retained.");
            }

            if (request.Verb == "trace")
                return new()
                {
                    Outcome = "Snapshot",
                    Reason = "Read-only file; it grants no authority.",
                    Trace = File.ReadLines(Path.Combine(root, "automation-trace.jsonl")).TakeLast(24).ToArray()
                };
            var file = Path.Combine(root, "automation-snapshot.json");
            if (new FileInfo(file).Length > AutomationFrames.ResponseLimit)
                throw new InvalidDataException();
            var snapshot = JsonSerializer.Deserialize(File.ReadAllBytes(file), AutomationJson.Default.AutomationSnapshot);
            return new()
            {
                Outcome = "Snapshot",
                Reason = "Read-only snapshot; check AtUtc and freshness. It grants no mutation authority.",
                Snapshot = snapshot
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return AutomationReply.Error("Unavailable", "No published observation is available.");
        }
    }
}

// A retained query-only handle identifies the original process even if its PID is reused.
internal sealed class ShutdownProcess : IDisposable
{
    private readonly SafeProcessHandle handle;
    private ShutdownProcess(SafeProcessHandle handle)
    {
        this.handle = handle;
    }

    internal static ShutdownProcess Open(int pid, long started)
    {
        var handle = OpenProcess(0x1000, false, checked((uint)pid));
        try
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!GetProcessTimes(handle, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (DateTime.FromFileTimeUtc(created).Ticks != started)
                throw new InvalidDataException("Process identity changed.");
            return new(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal Task WaitForExit(CancellationToken token) => WaitForExit(() =>
    {
        if (!GetExitCodeProcess(handle, out var code))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return code;
    }, token);
    internal static async Task WaitForExit(Func<uint> query, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var code = query();
            token.ThrowIfCancellationRequested();
            // An actual exit code of STILL_ACTIVE (259) is deliberately inconclusive.
            if (code != 259)
                return;
            await Task.Delay(25, token);
        }
    }

    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
