# CLI da overlay

`ApolloOverlay.Cli.exe` conversa com a instância em execução. Não inicia nem eleva a GUI, não repete pedidos e não aceita teclas, caminhos de executáveis ou scripts arbitrários.

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
