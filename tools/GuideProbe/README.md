# GuideProbe — observação temporária

`GuideProbe.exe --raw-guide-check --output NOVO.jsonl` é um diagnóstico opt-in de até60s. Primeiro copia somente os labels públicos v2 (elementos8bytes, count224/pointer232 em x64, máximo128); procura XboxGuide=1 sem adivinhar índice/bit. Sem candidato único termina cedo, sem pedir tentativas humanas. Ausência é `raw_label_unavailable`, não ausência global de Guide. Havendo candidato e ControllerButton anunciado, força leitura0x4 mesmo com Gamepad presente, exigindo kind/count/cópia completa. Primeiro estado é baseline; hold não repete DOWN. Reconnect relê descriptor e invalida a observação anterior. Nenhum Toggle/UI é chamado. Resultado positivo/negativo de entrega exige associação e tentativas humanas confirmadas pelo coordenador; o probe isolado preserva inconclusivo. Revisão independente é obrigatória antes da execução nativa.

.NET10, Windows x64. Não altera o produto nem configurações; não abre janela/foco, intercepta ou encaminha entrada. O Guide continua desligado no produto.

`GuideProbe.exe --self-test` executa somente casos sintéticos, sem GameInput/XInput reais.

Após revisão e autorização do coordenador: `GuideProbe.exe --duration-seconds 120 --output NOVO.jsonl`. Aceita90–120segundos, destino inexistente. Deadline monotônico; Ctrl+C cancela. Root deve iniciar oculto e capturar stdout/stderr. Falha de GameInput não bloqueia a observação XInput.

Guide usa exclusivamente GameInputv2 público e política local0x80. O callback retém temporariamente a referência COM do dispositivo antes de enfileirar; consumidor anonimiza via IUnknown, sem registrar ponteiro/serial. Identidades duram só esta execução, não associadas automaticamente aos slots XInput. Tempos nativos emµs, chegada monotônica emms e durações pareadas são distintos. Repetições, bordas órfãs, relógio regressivo, overflow ou hold sem soltura não estabelecem duração confiável. Limites: fila256,16dispositivos,6000registros ordinários, mais avisos/resumo obrigatórios.

Limpeza: desativar→Stop→Unregister→drenar→liberar identidades/objeto/DLL. Se Unregister falhar, contexto/objeto/DLL são retidos até a saída, com resultado incompleto. Sem ordinais privados, exclusividade, eixos, teclado ou títulos de aplicativos.

Contrato: header oficial retido `.codex/evidence/GameInput-v2.h`, SHA256 `2df7aa891696c72e1c7e6e3570e8aa3b2cc5629aefe5ae614386b5ad087f0830`; timestamp: https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/functions/gameinputsystembuttoncallback?view=gdk-2604 . Observação isolada não comprova preservação do toque curto Steam nem autoriza habilitar Guide.

## Diagnóstico de capacidades — 60 segundos

`GuideProbe.exe --device-check --output NOVO.jsonl` mantém as mesmas garantias de observação, sem ações no produto. Usa somente política local pública0xC0 (BackgroundInput+BackgroundGuide), enumeração inicial bloqueante GameInputv2 e callbacks de conexão. O modo passivo anterior continua com0x80 e90–120s.

Registra apenas família, tipos e bits de sistema anunciados, HRESULTs, IDs efêmeros e bordas comuns por dispositivo. Gamepad usa nomes documentados; Controller genérico usa índices ButtonN, limitados a128. Não lê nomes, PnP, VID/PID, GUID/serial, eixos ou teclas; não associa GameInput a slots XInput. Nenhuma capacidade anunciada comprova entrega do Guide.

`GetDeviceInfo` é HRESULT+out-pointer no header v2. O prefixo é calculado com Usage4bytes, Version8bytes e APP_LOCAL_DEVICE_ID32bytes; somente offsets92/96/104 (família/tipos/botões de sistema) são acessados. Referência do tamanho opaco32bytes: https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/lighting/lamparray/interfaces/ilamparray/methods/ilamparray_getdeviceid . Nenhum identificador opaco é copiado ou logado.

A classificação live preserva `inconclusive: human_association_required` quando há dispositivo/leitura, pois o executável não infere quem gerou as entradas. Root associa D-pad esquerda/direita ao relato humano e retém ambiguidade quando necessário. `no_relevant_device_observed` exige registro/enumeração completos, janela inteira sem perda/erro e nenhum relevante observado; não é veredito do transporte. `guide_unsupported` só corresponde ao suporte anunciado de dispositivo explicitamente associado sem ambiguidade. Cancelamento, leitura ausente/falha, info inválida, overflow, desconexão ou cleanup parcial têm precedência e resultam inconclusivos.

Todos os tokens são parados antes de remover qualquer um; todos precisam ser drenados antes de liberar contexto, dispositivos, leituras e DLL. Falha de registro parcial limpa tokens aceitos; falha de remoção retém contexto/runtime até a saída. Testes sintéticos exercitam também as assinaturas v2, out-pointer, leituras, reconexão, referências e classificação. Nenhum teste real pode ser iniciado antes da revisão independente/rootGO.
