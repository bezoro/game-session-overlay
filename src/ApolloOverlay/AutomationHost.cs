using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ApolloOverlay.Automation;

public interface IAutomationBackend
{
    Task<AutomationSnapshot> Snapshot(CancellationToken token, bool refreshTargets = false);
    Task<AutomationReply> Execute(AutomationRequest request, CancellationToken token, Action<AutomationReply> phase, Func<bool> continuing);
    Task Shutdown(CancellationToken token, Func<bool> continuing);
    Task<bool> Cancel(string id, string? grantedTarget, CancellationToken token) => Task.FromResult(false);
    Task<AutomationReply> Update(AutomationRequest request, CancellationToken token, Func<bool> continuing) => Task.FromResult(AutomationReply.Error("Unsupported", "Atualização por CLI indisponível nesta instância."));
    Task CommitUpdate(AutomationRequest request, CancellationToken token, Func<bool> continuing) => Task.FromException(new InvalidOperationException("Atualização por CLI indisponível nesta instância."));
}

public sealed record AutomationGrant(string TargetId, bool Shutdown);
internal sealed class AutomationLedger
{
    private sealed record Ticket(long Sequence, string Connection, long ExpiresTick);
    private readonly Dictionary<string, Ticket> tickets = [];
    private readonly Dictionary<string, AutomationRecord> records = [];
    private readonly string path;
    private readonly Func<long> clock;
    private long issued, retired;
    internal string Instance { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    internal AutomationLedger(string root, Func<long>? clock = null)
    {
        this.clock = clock ?? (() => Environment.TickCount64);
        path = Path.Combine(root, "automation-ledger.json");
        Directory.CreateDirectory(root);
        if (!File.Exists(path))
            return;
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("Ledger outside bounds.");
        var saved = JsonSerializer.Deserialize(File.ReadAllBytes(path), AutomationJson.Default.AutomationRecords) ?? throw new InvalidDataException("Ledger vazio.");
        if (saved.Records is null || saved.Records.Any(record => record is null || string.IsNullOrEmpty(record.Id) || record.Canonical is null || record.Reply is null))
            throw new InvalidDataException("Ledger inválido; histórico preservado.");
        foreach (var record in saved.Records.TakeLast(256))
            records[record.Id] = record.Terminal ? record : record with
            {
                Terminal = true,
                Reply = record.Reply with
                {
                    Outcome = "Unknown",
                    Reason = "Instância reiniciada após intenção persistida; efeito desconhecido, sem reenvio.",
                    Terminal = true,
                    Dispatched = null
                }
            };
        Save();
    }

    internal string Issue(string connection)
    {
        var seq = ++issued;
        var id = Instance + ":" + seq + ":" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        tickets[id] = new(seq, connection, clock() + 10000);
        return id;
    }

    internal void Retire(string connection)
    {
        foreach (var pair in tickets.Where(p => p.Value.Connection == connection).ToArray())
        {
            retired = Math.Max(retired, pair.Value.Sequence);
            tickets.Remove(pair.Key);
        }
    }

    internal AutomationRecord? Find(string id) => records.GetValueOrDefault(id);
    internal string Accept(string id, string connection, string canonical)
    {
        if (!tickets.TryGetValue(id, out var ticket) || ticket.Connection != connection || clock() > ticket.ExpiresTick)
            return "Rejected";
        if (records.TryGetValue(id, out var record))
            return record.Canonical == canonical ? "Duplicate" : "Rejected";
        if (ticket.Sequence <= retired)
            return "Rejected";
        retired = ticket.Sequence;
        while (records.Count >= 256)
        {
            var oldest = records.Values.Where(r => r.Terminal).OrderBy(r => r.AtUtc).FirstOrDefault();
            if (oldest is null)
                return "Busy";
            records.Remove(oldest.Id);
        }

        var pending = AutomationReply.Error("Pending", "Intenção persistida; nenhum efeito confirmado.", id) with
        {
            Terminal = false
        };
        records[id] = new(id, canonical, pending, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            Save();
        }
        catch
        {
            records.Remove(id);
            throw;
        }

        return "Accepted";
    }

    internal void Update(string id, AutomationReply reply)
    {
        if (records.TryGetValue(id, out var record))
        {
            records[id] = record with
            {
                Reply = reply with
                {
                    RequestId = id
                },
                Terminal = reply.Terminal
            };
            Save();
        }
    }

    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new AutomationRecords(records.Values.ToArray()), AutomationJson.Default.AutomationRecords);
        if (bytes.Length > 1024 * 1024)
            throw new InvalidDataException("Ledger outside bounds.");
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }

        File.Move(temporary, path, true);
    }
}

public sealed class AutomationHost : IDisposable
{
    private readonly IAutomationBackend backend;
    private readonly AutomationLedger ledger;
    private readonly string root, source;
    private readonly AutomationGrant? grant;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly List<NamedPipeServerStream> servers = [];
    private CancellationTokenSource? active;
    private AutomationRequest? activeRequest;
    private bool disposed;
    private int connections;
    public string Instance => ledger.Instance;
    public string PipeName
    {
        get;
    }
    public string? BridgeName => grant is null ? null : PipeName + ".bridge";

    public AutomationHost(IAutomationBackend backend, string root, string? name = null, AutomationGrant? grant = null)
    {
        this.backend = backend;
        this.root = root;
        this.grant = grant;
        ledger = new(root);
        PipeName = name ?? AutomationPipeSecurity.DefaultName;
        source = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "ApolloOverlay.dll")).ProductVersion ?? "unknown";
        var normal = AutomationPipeSecurity.Create(PipeName, false, true);
        lock (gate)
            servers.Add(normal);
        _ = AcceptLoop(normal, false);
        if (grant is not null)
        {
            try
            {
                var bridge = AutomationPipeSecurity.Create(BridgeName!, true, true);
                lock (gate)
                    servers.Add(bridge);
                _ = AcceptLoop(bridge, true);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        _ = PublishInitial();
    }

    private async Task PublishInitial()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            Publish((await backend.Snapshot(stopping.Token)) with
            {
                Instance = Instance,
                Pid = Environment.ProcessId,
                Started = process.StartTime.ToUniversalTime().Ticks,
                SourceRevision = source
            });
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
        }
    }

    private async Task AcceptLoop(NamedPipeServerStream pipe, bool restricted)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stopping.Token);
                var connected = pipe;
                pipe = AutomationPipeSecurity.Create(restricted ? BridgeName! : PipeName, restricted, false);
                lock (gate)
                    servers.Add(pipe);
                _ = Serve(connected, restricted);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            pipe.Dispose();
        }
    }

    private bool Granted(AutomationRequest request) => grant is not null && (request.Verb == "shutdown" ? grant.Shutdown : request.Verb == "execute" && request.ActionId is "focus" or "steam-overlay" && request.TargetId == grant.TargetId && request.Preset.Length == 0 && request.Intent == "activate");
    private static bool Query(string verb) => verb is "status" or "targets" or "actions" or "trace" or "result" or "update-status";
    private static bool UpdateCommand(string verb) => verb is "update-check" or "update-download" or "update-rollback" or "update-cancel" or "update-apply";
    private static bool Valid(AutomationRequest r) => r.Version == 1 && r.RequestId is { Length: <= 256 } && r.ActionId is { Length: <= 256 } && r.TargetId is { Length: <= 128 } && r.Preset is { Length: <= 64 } && r.BuildId is { Length: <= 96 } && r.JobId is { Length: <= 256 } && r.Channel is "" or "testing" or "stable" && r.Intent is "activate" or "left" or "right" && (Query(r.Verb) || UpdateCommand(r.Verb) || r.Verb is "execute" or "cancel" or "shutdown");
    private async Task Serve(NamedPipeServerStream pipe, bool restricted)
    {
        if (Interlocked.Increment(ref connections) > 8)
        {
            Interlocked.Decrement(ref connections);
            lock (gate)
                servers.Remove(pipe);
            pipe.Dispose();
            return;
        }

        var connection = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        string ticket = "";
        var expires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000;
        using var life = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        life.CancelAfter(10000);
        Task? monitor = null;
        using var writing = new SemaphoreSlim(1, 1);
        async Task Reply(AutomationReply value)
        {
            await writing.WaitAsync(life.Token);
            try
            {
                await AutomationFrames.Write(pipe, AutomationFrames.Encode(value), AutomationFrames.ResponseLimit, life.Token);
            }
            finally
            {
                writing.Release();
            }
        }

        try
        {
            if (!AutomationPipeSecurity.ClientAllowed(pipe))
                return;
            lock (gate)
                ticket = ledger.Issue(connection);
            var process = Process.GetCurrentProcess();
            await Reply(new()
            {
                Outcome = "Handshake",
                Ticket = ticket,
                Instance = Instance,
                Pid = Environment.ProcessId,
                Started = process.StartTime.ToUniversalTime().Ticks,
                ExpiresUtc = expires
            });
            var request = JsonSerializer.Deserialize(await AutomationFrames.Read(pipe, AutomationFrames.RequestLimit, life.Token), AutomationJson.Default.AutomationRequest);
            if (request is null || !Valid(request))
            {
                await Reply(AutomationReply.Error("Rejected", "Protocolo ou intenção inválida."));
                return;
            }

            if (Query(request.Verb))
            {
                if (request.Verb == "update-status")
                {
                    var state = await backend.Update(request, life.Token, () => AutomationPipeSecurity.Alive(pipe));
                    await Reply(state with
                    {
                        Instance = Instance,
                        Pid = Environment.ProcessId,
                        Started = process.StartTime.ToUniversalTime().Ticks,
                        Session = process.SessionId
                    });
                    return;
                }

                if (request.Verb == "result")
                {
                    AutomationRecord? record;
                    lock (gate)
                        record = ledger.Find(request.RequestId);
                    await Reply(record?.Reply ?? AutomationReply.Error("Unavailable", "ID não retido."));
                    return;
                }

                if (request.Verb == "trace")
                {
                    await Reply(new()
                    {
                        Outcome = "Completed",
                        Trace = File.Exists(Path.Combine(root, "automation-trace.jsonl")) ? File.ReadLines(Path.Combine(root, "automation-trace.jsonl")).TakeLast(24).ToArray() : []
                    });
                    return;
                }

                var snapshot = await backend.Snapshot(life.Token, request.Verb == "targets");
                snapshot = snapshot with
                {
                    Instance = Instance,
                    Pid = Environment.ProcessId,
                    Started = process.StartTime.ToUniversalTime().Ticks,
                    SourceRevision = source
                };
                if (restricted)
                    snapshot = snapshot with
                    {
                        Actions = snapshot.Actions.Select(a => a.Id is "focus" or "steam-overlay" ? a : a with { Available = false, Capability = "Denied", Reason = "Ponte restrita à concessão explícita." }).ToArray()
                    };
                await Reply(new()
                {
                    Outcome = "Completed",
                    Snapshot = snapshot
                });
                return;
            }

            if (request.Verb == "cancel")
            {
                bool found;
                lock (gate)
                {
                    found = activeRequest?.RequestId == request.RequestId && (!restricted || Granted(activeRequest));
                    if (found)
                        active?.Cancel();
                }

                if (!found)
                    found = await backend.Cancel(request.RequestId, restricted ? grant!.TargetId : null, life.Token);
                await Reply(AutomationReply.Error(found ? "Cancelled" : "Unavailable", found ? "Cancelamento solicitado; limpeza ainda não confirmada. Consulte result até Terminal=true." : "Solicitação não ativa ou não concedida.", request.RequestId) with
                {
                    Terminal = !found,
                    Dispatched = found ? null : false
                });
                return;
            }

            if (request.Preset.Length > 0)
            {
                await Reply(AutomationReply.Error("Unsupported", "Preset diagnóstico não integrado; nenhum foco/atalho enviado.", request.RequestId));
                return;
            }

            if (restricted && !Granted(request))
            {
                await Reply(AutomationReply.Error("Denied", "Ponte permite somente o alvo e capacidades concedidos nesta execução.", request.RequestId));
                return;
            }

            if (request.DryRun)
            {
                await Reply(UpdateCommand(request.Verb) ? await backend.Update(request, life.Token, () => AutomationPipeSecurity.Alive(pipe)) : await backend.Execute(request, life.Token, _ =>
                {
                }, () => AutomationPipeSecurity.Alive(pipe)));
                return;
            }

            if (request.Verb == "execute" || UpdateCommand(request.Verb))
            {
                var preflight = UpdateCommand(request.Verb) ? await backend.Update(request with
                {
                    DryRun = true
                }, life.Token, () => AutomationPipeSecurity.Alive(pipe)) : await backend.Execute(request with
                {
                    DryRun = true
                }, life.Token, _ =>
                {
                }, () => AutomationPipeSecurity.Alive(pipe));
                if (preflight.Outcome != "DryRun")
                {
                    await Reply(preflight with
                    {
                        RequestId = request.RequestId
                    });
                    return;
                }
            }

            if (request.RequestId != ticket || request.DeadlineUtc <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                await Reply(AutomationReply.Error("Rejected", "Ticket incorreto ou expirado.", request.RequestId));
                return;
            }

            var canonical = JsonSerializer.Serialize(request, AutomationJson.Default.AutomationRequest);
            string accepted;
            lock (gate)
            {
                if (active is not null)
                    accepted = "Busy";
                else
                {
                    accepted = ledger.Accept(request.RequestId, connection, canonical);
                    if (accepted == "Accepted")
                    {
                        active = life;
                        activeRequest = request;
                    }
                }
            }

            if (accepted != "Accepted")
            {
                AutomationRecord? duplicate;
                lock (gate)
                    duplicate = ledger.Find(request.RequestId);
                await Reply(accepted == "Duplicate" ? duplicate!.Reply : AutomationReply.Error(accepted, "Solicitação não aceita; não há retry automático.", request.RequestId));
                return;
            }

            life.CancelAfter((int)Math.Clamp(Math.Min(request.DeadlineUtc, expires) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 1, 10000));
            Trace(request, "Intent", AutomationReply.Error("Pending", "", request.RequestId) with
            {
                Terminal = false
            });
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
            monitor = Watch();
            async Task Watch()
            {
                try
                {
                    while (true)
                    {
                        var bytes = await AutomationFrames.Read(pipe, AutomationFrames.RequestLimit, reading.Token);
                        var command = JsonSerializer.Deserialize(bytes, AutomationJson.Default.AutomationRequest);
                        if (command?.Verb == "cancel" && command.RequestId == request.RequestId)
                        {
                            life.Cancel();
                            return;
                        }

                        if (command?.RequestId != request.RequestId || JsonSerializer.Serialize(command, AutomationJson.Default.AutomationRequest) != canonical)
                        {
                            life.Cancel();
                            return;
                        }

                        AutomationRecord? duplicate;
                        lock (gate)
                            duplicate = ledger.Find(request.RequestId);
                        if (duplicate is not null)
                            await Reply(duplicate.Reply);
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
                {
                    if (!reading.IsCancellationRequested)
                        life.Cancel();
                }
            }

            AutomationReply result;
            if (request.Verb == "update-apply")
            {
                var commitAttempted = false;
                try
                {
                    result = await backend.Update(request, life.Token, () => AutomationPipeSecurity.Alive(pipe));
                    result = result with
                    {
                        RequestId = request.RequestId,
                        Instance = Instance,
                        Pid = Environment.ProcessId,
                        Started = process.StartTime.ToUniversalTime().Ticks,
                        Session = process.SessionId
                    };
                    lock (gate)
                        ledger.Update(request.RequestId, result);
                    Trace(request, "UpdateApplyRequested", result);
                    await Reply(result);
                    if (result.Outcome != "UpdateApplyRequested")
                        return;
                    await Task.Delay(50, life.Token);
                    life.Token.ThrowIfCancellationRequested();
                    if (!AutomationPipeSecurity.Alive(pipe))
                        return;
                    commitAttempted = true;
                    await backend.CommitUpdate(request, life.Token, () => !life.IsCancellationRequested && AutomationPipeSecurity.Alive(pipe));
                    return;
                }
                finally
                {
                    result = AutomationReply.Error(commitAttempted ? "Unknown" : "Cancelled", commitAttempted ? "Reinício pode ter sido solicitado; confirme a versão em uma nova conexão." : "Cancelado antes de aplicar a atualização.", request.RequestId);
                    lock (gate)
                        ledger.Update(request.RequestId, result);
                    Trace(request, "Result", result);
                    reading.Cancel();
                }
            }

            if (request.Verb == "shutdown")
            {
                var commitAttempted = false;
                try
                {
                    result = AutomationReply.Error("ShutdownRequested", "Fechamento normal solicitado; cliente deve observar a saída exata.", request.RequestId) with
                    {
                        Terminal = false
                    };
                    lock (gate)
                        ledger.Update(request.RequestId, result);
                    Trace(request, "ShutdownRequested", result);
                    await Reply(result);
                    await Task.Delay(50, life.Token);
                    life.Token.ThrowIfCancellationRequested();
                    if (!AutomationPipeSecurity.Alive(pipe))
                        return;
                    commitAttempted = true;
                    await backend.Shutdown(life.Token, () => !life.IsCancellationRequested && AutomationPipeSecurity.Alive(pipe));
                    return;
                }
                finally
                {
                    result = AutomationReply.Error(commitAttempted ? "Unknown" : "Cancelled", commitAttempted ? "Fechamento pode ter sido solicitado; cliente deve observar a saída exata." : "Cancelado antes de solicitar o fechamento.", request.RequestId);
                    lock (gate)
                        ledger.Update(request.RequestId, result);
                    Trace(request, "Result", result);
                    reading.Cancel();
                }
            }

            try
            {
                result = UpdateCommand(request.Verb) ? await backend.Update(request, life.Token, () => !life.IsCancellationRequested && AutomationPipeSecurity.Alive(pipe)) : await backend.Execute(request, life.Token, phase =>
                {
                    lock (gate)
                        ledger.Update(request.RequestId, phase with
                        {
                            Terminal = false
                        });
                    Trace(request, "Phase", phase);
                }, () => !life.IsCancellationRequested && AutomationPipeSecurity.Alive(pipe));
            }
            catch (OperationCanceledException)
            {
                result = AutomationReply.Error("Cancelled", "Cancelado; consulte evidência de envio.", request.RequestId);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                result = AutomationReply.Error("Failed", e.GetType().Name, request.RequestId);
            }

            // The acceptance request is complete; the independent job is tracked by
            // Update.JobId/Terminal and the persisted update receipt, not a pipe ticket.
            result = result with
            {
                RequestId = request.RequestId,
                Terminal = UpdateCommand(request.Verb) && result.Outcome == "Requested" || result.Terminal
            };
            lock (gate)
                ledger.Update(request.RequestId, result);
            Trace(request, "Result", result);
            reading.Cancel();
            await Reply(result);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException)
        {
            life.Cancel();
        }
        finally
        {
            life.Cancel();
            lock (gate)
            {
                ledger.Retire(connection);
                if (ReferenceEquals(active, life))
                {
                    active = null;
                    activeRequest = null;
                }

                servers.Remove(pipe);
            }

            pipe.Dispose();
            Interlocked.Decrement(ref connections);
            if (monitor is not null)
                try
                {
                    await monitor;
                }
                catch (OperationCanceledException)
                {
                }
        }
    }

    private void Publish(AutomationSnapshot snapshot)
    {
        lock (gate)
        {
            var path = Path.Combine(root, "automation-snapshot.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, AutomationJson.Default.AutomationSnapshot);
            if (bytes.Length > AutomationFrames.ResponseLimit)
                return;
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, true);
        }
    }

    internal void ObserveSnapshot(AutomationSnapshot snapshot)
    {
        try
        {
            Publish(snapshot with
            {
                Instance = Instance,
                SourceRevision = source
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal string? BeginUi(string action, string target)
    {
        lock (gate)
        {
            if (active is not null)
                return null;
            var connection = "ui:" + Guid.NewGuid().ToString("N");
            var id = ledger.Issue(connection);
            var request = new AutomationRequest
            {
                Verb = "execute",
                ActionId = action,
                TargetId = target,
                RequestId = id
            };
            try
            {
                if (ledger.Accept(id, connection, JsonSerializer.Serialize(request, AutomationJson.Default.AutomationRequest)) != "Accepted")
                    return null;
                Trace(request, "UI-Intent", AutomationReply.Error("Pending", "", id) with
                {
                    Terminal = false
                });
                return id;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                ledger.Retire(connection);
            }
        }
    }

    internal void UiResult(string id, string target, AutomationReply result)
    {
        lock (gate)
            ledger.Update(id, result);
        Trace(new()
        {
            RequestId = id,
            TargetId = target,
            ActionId = "steam-overlay",
            Verb = "execute"
        }, result.Terminal ? "UI-Result" : "UI-Phase", result);
    }

    private void Trace(AutomationRequest request, string phase, AutomationReply result)
    {
        lock (gate)
        {
            var path = Path.Combine(root, "automation-trace.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
                File.Move(path, path + ".1", true);
            var entry = new AutomationTrace(Instance, request.RequestId, phase, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), source, request.TargetId, Native.GetForegroundWindow().ToInt64(), result.Dispatched, result.Expected, result.Accepted, result.Outcome, result.Sequence);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, AutomationJson.Default.AutomationTrace) + "\n");
            stream.Write(bytes);
            stream.Flush(true);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            stopping.Cancel();
            active?.Cancel();
            foreach (var pipe in servers.ToArray())
                pipe.Dispose();
            servers.Clear();
        }
    }
}
