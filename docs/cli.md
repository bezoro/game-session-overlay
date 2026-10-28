# CLI da overlay

`gso.cmd` e `ApolloOverlay.Cli.exe` vêm no aplicativo instalado e conversam com a instância em execução, mesmo com a overlay oculta. Não iniciam nem elevam a GUI, não repetem pedidos e não aceitam teclas, caminhos de executáveis ou scripts arbitrários.

## Atualizar pelo terminal ou por um agente

Instale uma vez pelo Setup atual para receber a CLI e a API de atualização. Uma instalação antiga sem esses componentes precisa desse primeiro passo. Depois, os comandos usam o mesmo atualizador Velopack da interface. A consulta e o download das publicações públicas não exigem login ou token do GitHub.

No PowerShell, encontre a CLI da instalação atual:

```powershell
$install = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GameSessionOverlay').InstallLocation
$gso = Join-Path $install 'current\gso.cmd'
& $gso version
& $gso update check --channel testing
& $gso update --channel testing
```

Execute no mesmo usuário, sessão e nível de elevação da overlay. Se ela estiver elevada, o terminal ou agente também precisa estar elevado. A janela pode permanecer oculta; os comandos de atualização não dependem de foco nem de navegação na interface. O instalador não adiciona `gso` ao PATH: use o caminho encontrado acima ou execute `gso.cmd` da pasta `current`.

| Comando | Resultado |
|---|---|
| `gso version` | Identidade da instância que está executando agora. |
| `gso update status` | Versão atual, instalação, operação, progresso, catálogo e histórico. |
| `gso update check --channel testing` | Consulta as publicações do canal, sem instalar. Aceita também `stable`. |
| `gso update` | Consulta o canal selecionado, baixa a versão escolhida e solicita aplicação com reinício. |
| `gso update install --build BUILD_ID --channel testing` | Seleciona uma publicação exata do catálogo, inclusive uma anterior. Sem `--build`, escolhe a publicação mais recente do canal. |
| `gso history` | Lista as versões preservadas localmente para retorno. |
| `gso rollback BUILD_ID` | Prepara a versão preservada indicada e solicita aplicação com reinício. |
| `gso update cancel JOB_ID` | Solicita cancelamento da preparação correspondente. Não desfaz uma aplicação já solicitada. |

Cada resposta é um objeto JSON. `Update.Version`, `BuildId`, `Commit` e `Channel` descrevem o binário da instância consultada; `Pid`, `Started`, `Session` e `Instance` identificam essa execução. `Update.TargetBuildId` e `TargetVersion` descrevem o destino preparado. `JobId`, `Phase`, `Progress`, `Terminal`, `Available` e `History` permitem acompanhar o trabalho. Salve ou descarte ajustes pendentes antes de trocar de versão.

Uma resposta `Outcome: Requested` à atualização ou ao rollback confirma o pedido ao Velopack e a saída da instância anterior. **Ela ainda não confirma que a nova versão iniciou.** O processo da CLI termina para permitir a substituição dos binários. Consulte a instalação novamente com uma nova execução de `gso version`; apenas uma resposta fresca `Completed`, com o `Update.BuildId` esperado e uma nova identidade de processo, confirma o destino em execução. A consulta pode retornar `Unavailable` durante o reinício. Não há um `--wait` que atravesse a substituição dos executáveis.

Um agente pode usar este fluxo, sem reenviar a mutação:

```powershell
$before = (& $gso version | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or $before.Outcome -ne 'Completed') { throw 'Instância atual indisponível.' }
$requested = (& $gso update --channel testing | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw $requested.Reason }
if ($requested.Outcome -eq 'Requested') {
    $target = $requested.Update.TargetBuildId
    $confirmed = $false
    $limit = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 500
        $after = (& $gso version | ConvertFrom-Json)
        if ($LASTEXITCODE -eq 0 -and $after.Outcome -eq 'Completed' -and
            $after.Update.BuildId -eq $target -and
            ($after.Pid -ne $before.Pid -or $after.Started -ne $before.Started)) {
            $confirmed = $true
            break
        }
    } while ($limit.Elapsed.TotalSeconds -lt 60)
    if (-not $confirmed) { throw 'Destino ainda não confirmado. Consulte version/status; não repita a instalação automaticamente.' }
    $after
} else {
    $requested
}
```

`--timeout 5..600` limita o tempo de preparação da CLI, em segundos; o padrão é 180. `--dry-run` valida um pedido de mudança sem executá-lo. Para acompanhamento ou cancelamento de uma preparação em andamento, use outro terminal com `update status` ou `update cancel JOB_ID`. Um resultado `Unknown` exige observar `version` e `update status` antes de decidir o próximo passo. O wrapper preserva o código de saída da CLI; código zero também pode acompanhar `Requested` e não substitui a confirmação da versão.

## Ações da sessão

```powershell
ApolloOverlay.Cli.exe status
ApolloOverlay.Cli.exe targets
ApolloOverlay.Cli.exe actions
ApolloOverlay.Cli.exe execute route:integrations
ApolloOverlay.Cli.exe execute focus --target ID --dry-run
ApolloOverlay.Cli.exe execute steam-overlay --target ID
ApolloOverlay.Cli.exe result REQUEST_ID
ApolloOverlay.Cli.exe cancel REQUEST_ID
ApolloOverlay.Cli.exe trace
ApolloOverlay.Cli.exe shutdown
```

`targets` atualiza as observações quando não há operação ativa e fornece identidades opacas ligadas a HWND/PID/criação/geração; `TargetsAtUtc` registra a última atualização, separada de `AtUtc`. `actions` lista os controles reais da rota, disponibilidade, motivo, capacidade e intenções. `--intent left|right` existe somente nos ajustes anunciados. Rotas finitas: `route:home`, `apps`, `system`, `audio`, `integrations`, `options`, `details`, `customize`, `setup`, `result`; options/details requerem `--target ID`. Um target explícito antigo é rejeitado, nunca substituído silenciosamente por outro.

| Capacidade | Comportamento |
|---|---|
| `Execute` | Handler síncrono seguro ou dono cancelável de foco/Steam. |
| `RequiresGesture` | Fechamento com pressão longa; exige a interação normal, sem bypass. |
| `RequiresInteractive` | File picker, inicialização/atalhos legados, controles `operation-details` e `dismiss-operation`, e operações assíncronas ainda sem guard de cliente. A CLI não invoca esses handlers. |
| `Unavailable`, `Denied`, `Unsupported` | Nada enviado. O motivo acompanha a resposta. `diagnostic-dwell` é Unsupported neste estágio. |

O atalho Steam solicita foco uma vez e confirma a identidade e 500 ms de primeiro plano. Para a configuração `Shift+Tab`, envia quatro eventos separados: Shift-down, pelo menos 20 ms, Tab-down, pelo menos 60 ms, Tab-up, pelo menos 20 ms e Shift-up. `Ctrl+Tab` conserva o lote anterior. As esperas revalidam entrada, conexão, alvo, foco, prazo e cancelamento em fatias de até 10 ms. A origem UI mantém o handoff de liberação; a CLI qualifica entrada neutra independentemente.

Cancelar, reabrir, perder contexto ou exceder o prazo proíbe novos downs imediatamente. O dono mantém exclusão até terminar a limpeza limitada das teclas possivelmente enviadas. `cancel` confirma somente a solicitação de interrupção (`Terminal=false`); consulte `result` até o término. O fechamento normal aguarda essa drenagem sem bloquear a janela. Não existe garantia de limpeza após crash ou encerramento forçado.

`Requested` exige quatro eventos aceitos e liberação observada, sem confirmar que Steam apareceu. `Partial` indica envio incompleto conhecido com teclas liberadas; `Unknown` indica contagem ou liberação incerta. O campo opcional `Sequence` em result/trace registra configuração, intervalos mínimos, foco qualificado, até quatro eventos normais e dois ups de limpeza, offsets monotônicos, chamadas iniciadas, contagens e máscara final. Cleanup tem contagem separada. Repouso físico é necessário: a leitura não distingue perfeitamente Shift/Tab físicos das mesmas teclas injetadas. Não repita um toggle incerto; examine `result`, `trace` e evidência visual antes de estabelecer novamente o estado inicial.

Cada mutação recebe ticket do servidor, válido na conexão por até dez segundos. IDs antigos, expirados, desconectados, alterados ou expulsos do ledger não executam novamente. Intenções persistidas sem resultado terminal recuperam como `Unknown` após reinício; não há redispatch. `--dry-run` valida sem handler mutante/intenção durável. Resultados de prévia são `Simulated`.

A pipe normal exige mesmo usuário **e elevação**, logon e sessão. Sem acesso à instância High, consultas podem retornar arquivos de observação, marcados `Snapshot` e com `AtUtc`; isso não autoriza mutações e pode estar antigo.

## Ponte restrita, opcional por execução

Desligada por padrão e no autostart. Somente após revisão dos binários e autorização explícita, uma execução elevada pode receber:

```text
ApolloOverlay.exe --automation-bridge PID:START_TICKS:HWND [--allow-self-shutdown]
```

O alvo é resolvido à identidade completa atual. A ponte permite exclusivamente foco, atalho Steam configurado daquele alvo e, se concedido, encerramento da própria instância. Ela nega launch, paths, settings, áudio, outros alvos e presets. O cliente usa `--bridge`; não existe autoelevação. Um jogo reiniciado exige uma nova concessão. A verificação efetiva Medium→High e o comportamento real no jogo são gates de entrega autorizada, distintos dos testes em fixtures Medium.

`shutdown` responde `ShutdownRequested`, chama o fechamento normal e só retorna `Completed` após observar a saída do PID/criação exatos. Não há force-kill. Cancelamento anterior ao commit impede Close e grava resultado terminal `Cancelled`; após tentativa de commit, o ledger registra `Unknown` e a saída exata continua sendo observada pelo cliente. Depois do commit, cancelar não pode desfazer o encerramento.

Cobertura não é universal: controladores físicos, áudio real, diálogos/gestos, IPC entre integridades e visibilidade Steam precisam das respectivas observações. A suíte de fixtures não afirma que todas essas funções foram exercitadas.
