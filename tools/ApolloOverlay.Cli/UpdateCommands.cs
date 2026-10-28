using System.ComponentModel;
using System.IO;
using System.Text.Json;
using ApolloOverlay.Automation;

internal static class UpdateCommands
{
    private sealed record Options(string Command, string Pipe, string Channel, string BuildId, string JobId, bool DryRun, int TimeoutSeconds);
    internal static async Task<int> Run(string[] args)
    {
        if (args.Any(arg => arg is "--help" or "-h"))
        {
            Console.WriteLine("gso version | history | update [install|check|status|cancel JOB_ID] [--build BUILD_ID] [--channel testing|stable] [--dry-run] [--timeout 5..600] | rollback BUILD_ID. Saída JSON. update solicita o reinício; confirme a versão em uma nova execução de gso version. Nenhum lançamento ou elevação implícitos.");
            return 0;
        }

        return Program.Print(await Execute(args, Program.Send, CancellationToken.None));
    }

    internal static async Task<AutomationReply> Execute(string[] args, Func<string, AutomationRequest, CancellationToken, Task<AutomationReply>> send, CancellationToken cancellation, string? defaultPipe = null, Func<CancellationToken, Task>? pause = null)
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
                    return AutomationReply.Error("Unknown", "O servidor não identificou a operação iniciada. Consulte update status antes de prosseguir.");
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
                        return AutomationReply.Error("Unknown", "A instância mudou durante a preparação. Consulte a versão; nenhum novo pedido será enviado.", job) with
                        {
                            Update = reply.Update
                        };
                    if (reply.Update.JobId != job)
                        return AutomationReply.Error("Unknown", "Outra operação substituiu o pedido. Nenhuma instalação será solicitada.", job) with
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
                    Reason = "Uma observação antiga não autoriza atualização. Conecte-se à instância em execução."
                };
            if (current.Outcome != "Completed" || current.Update is null)
                return current.Outcome == "Completed" ? AutomationReply.Error("Unsupported", "A instância não oferece a API de atualização. Instale o Setup mais recente.") : current;
            if (options.Command is "version" or "status" or "history")
                return current;
            if (options.Command != "check" && (!current.Update.Installed || current.Update.SettingsDirty))
                return current with
                {
                    Outcome = "Rejected",
                    Reason = current.Update.SettingsDirty ? "Salve ou descarte os ajustes antes de trocar de versão." : "Instale a versão inicial pelo Setup antes de atualizar pela CLI."
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
                        Reason = "Nenhuma publicação correspondente foi encontrada no canal selecionado."
                    };
                if (selected.BuildId == ready.Update.BuildId)
                    return ready with
                    {
                        Reason = "A versão selecionada já está em execução."
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
                    Reason = "Os ajustes mudaram durante o download. Salve ou descarte antes de aplicar."
                };
            if (ready.Update.TargetBuildId.Length == 0)
                return ready with
                {
                    Outcome = "Rejected",
                    Reason = "O pacote preparado não identifica a versão de destino."
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

            return AutomationReply.Error("Unknown", applying ? "Confirmação interrompida após solicitar aplicação. Consulte gso version e update status; não repita automaticamente." : "Prazo ou cancelamento durante a preparação. Nenhuma aplicação foi solicitada; consulte update status.", job);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or Win32Exception or TimeoutException)
        {
            return AutomationReply.Error(applying ? "Unknown" : "Unavailable", "A instância local não respondeu. Confira se está em execução no mesmo usuário, sessão e nível de elevação. Nenhum lançamento ou reenvio foi feito.", job);
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
            throw new ArgumentException("Informe um comando.");
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
                    throw new ArgumentException("Informe o JobId.");
                job = args[index++];
            }
        }
        else if (command == "rollback")
        {
            if (index >= args.Length)
                throw new ArgumentException("Informe o BuildId preservado.");
            build = args[index++];
        }

        if (command is not ("version" or "history" or "status" or "install" or "check" or "cancel" or "rollback"))
            throw new ArgumentException("Comando de atualização desconhecido.");
        var pipe = defaultPipe ?? "";
        var channel = "";
        var dry = false;
        var seconds = 180;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option))
                throw new ArgumentException("Opção repetida: " + option);
            if (option == "--dry-run")
            {
                dry = true;
                continue;
            }

            if (++index >= args.Length)
                throw new ArgumentException("Falta o valor de " + option);
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
                    throw new ArgumentException("Opção não suportada: " + option);
            }
        }

        if (channel.Length > 0 && (command is not ("install" or "check") || channel is not ("testing" or "stable")))
            throw new ArgumentException("Canal inválido para este comando.");
        if (seconds is < 5 or > 600)
            throw new ArgumentException("O prazo deve ficar entre 5 e 600 segundos.");
        if (seen.Contains("--build") && build.Length == 0 || build.Length > 0 && (build.Length > 96 || build.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))))
            throw new ArgumentException("BuildId inválido.");
        if (command == "rollback" && build.Length == 0 || command == "cancel" && (job.Length == 0 || job.Length > 256 || job.Any(char.IsControl)))
            throw new ArgumentException("Identificador ausente ou inválido.");
        if (dry && command is "version" or "history" or "status")
            throw new ArgumentException("Consulta de estado já é somente leitura.");
        if (pipe.Length == 0)
            pipe = AutomationPipeSecurity.DefaultName;
        if (pipe.Length > 128 || pipe.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ArgumentException("Somente endpoints locais são aceitos.");
        return new(command, pipe, channel, build, job, dry, seconds);
    }
}
