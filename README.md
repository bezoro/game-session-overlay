# Bezoro · Controle da sessão

Overlay nativa WPF/.NET 10 para Windows 11. Não requer administrador, SDK nem instalação no Windows quando executada pelo pacote self-contained.

No pacote, dê duplo clique em **Abrir overlay.cmd** para iniciar e abrir. Depois, dê **duplo clique esquerdo no ícone da bandeja** ou use seu menu **Abrir overlay / Sair**. `ApolloOverlay.exe` inicia oculto; `--show` abre uma instância nova. Se já houver uma instância em execução, a mensagem orienta usar a bandeja. Nenhuma inicialização automática é registrada.

- Controle: Guide configurado abre na soltura de um toque curto e fecha com proteção de soltura; a bandeja permanece disponível. Direcional/analógico navega, A seleciona, B retorna, X mostra detalhes, LB/RB trocam abas. Ctrl+Alt+Espaço e LB+RB+Start não ativam mais a overlay.
- Teclado: setas, Tab/Shift+Tab, Enter/Espaço, Esc, X e PageUp/PageDown. O teclado consegue alcançar todas as opções.
- Fechar janela: solte e mantenha uma nova pressão de A/Enter por1,5s. Soltar, B, desconectar ou perder foco cancela. Steam, Playnite, Explorer e janelas técnicas são protegidos. Não há encerramento forçado.
- Áudio: controla a saída padrão do Windows, incluindo volume e mudo; não garante volume físico da TV nem endpoint do streaming.
- Playnite: descoberta local ou caminho pelo Sistema. Steam: protocolo público Big Picture. Atalhos de Game Bar/Steam/Visão de tarefas são solicitações; visibilidade e controle externo não são presumidos.

Antes de ocultar, alternar aplicativo, enviar um atalho ou encerrar pela interface, a overlay aguarda todos os controles e teclas soltos por100ms contínuos. Input novo reinicia essa espera. B/Esc cancela ações pendentes; quando já significam dispensar o painel, B/Esc/Guide reafirmam a saída. Se foco, leitura do controle ou identidade do destino ficar incerta, a restauração e os efeitos externos daquele pedido são descartados permanentemente. A saída local continua aguardando qualquer tecla, botão, stick ou gatilho ainda observado ativo e100ms após a liberação. Se uma entrada antes ativa deixar de ser legível, pressione novamente Escape e solte para autorizar apenas a ocultação local, aguardando também as demais entradas legíveis; repetir a pressão original não autoriza essa saída. Leitura desconhecida não comprova soltura nem consumo do gesto. No caminho normal, a espera evita ceder foco ainda com o gesto pressionado; não bloqueia XInput em aplicativos que leem o controle em segundo plano. **Voltar ao jogo** usa a sessão de jogo preservada, não o launcher à frente. **Visão de tarefas** solicita Win+Tab mesmo sem jogo. O acesso rápido Steam solicita o menu por F1 quando o contexto anterior é Big Picture verificado; em jogo com módulo Steam confirmado, solicita o overlay Steam. Esses envios não confirmam que o menu foi aberto.

A interface acompanha automaticamente claro/escuro e alto contraste do Windows por recursos Fluent dinâmicos. Há animações breves e opção de redução. Sem capas inventadas, captura contínua, injeção em jogos, driver ou telemetria.

**Guide/Xbox é opt-in experimental:** `GuideXInputEnabled:true` no JSON (padrão `false`) habilita a leitura legada não documentada do XInput. Oculta, a overlay abre apenas ao soltar um toque válido após uma soltura basal; o limite padrão é2,0s. Segurar além do limite, desconectar ou falhar a leitura não abre. Sistema oferece slider de0,5–3,0s, passo0,1s; esquerda/direita ajusta, A em **Salvar ajustes** persiste. O limite é capturado no início de cada pressão. Guide visível dispensa com a mesma proteção de soltura de B/Esc. A leitura já foi observada em Playnite; as mudanças de soltura/foco/tray desta versão ainda requerem validação física. A overlay não bloqueia Guide em outros programas: os atalhos Guide da Steam e as opções de controle da Game Bar precisam permanecer desativados para evitar ações concorrentes; Win+G e Win+Tab continuam disponíveis. Foco confirmado não isola XInput de jogos que leem o controle em segundo plano.

Cada abertura solicita foco uma vez e só habilita navegação/ações após observar a própria janela à frente. Recusa ou perda de foco deixa B/Esc disponíveis para dispensa segura; um novo pedido de Guide/bandeja tenta novamente. A confirmação de foco é limitada, sem insistência contínua. Diálogo do Playnite retorna por novo ciclo confirmado preservando página, seleção e sessão. O fade breve aplica-se à janela inteira, com opção de redução e respeito às animações/alto contraste do Windows.

Ajustes são explicitamente salvos em `%LOCALAPPDATA%\Bezoro\GameSessionOverlay\settings.json`; configuração anterior e fonte corrompida são preservadas. Sistema permite mudar limite Guide, deadzone, monitor, animações, atalho Steam e caminhos. JSON versão2 aceita versão1 sem reescrever automaticamente; campos antigos de ativação são mantidos por compatibilidade, mas não ativam nada. `GuideOpenMaxHoldMs` ausente/inválido usa2000 sem descartar os demais ajustes válidos. Logs locais rotativos limitam-se a três arquivos de aproximadamente512KiB, sem títulos/caminhos privados; checkpoints de foco registram resultado nativo e confirmação observada, sem log por frame.

`--preview` é diagnóstico com ações de janelas simuladas e contexto vazio; não representa um jogo. A operação normal usa contexto e ações reais. `--capture-native DIRETÓRIO`, `--capture-alpha DIRETÓRIO` e `--native-check DIRETÓRIO` são verificações limitadas a janelas próprias; a última inclui teste reversível de áudio.

## Compilar e testar

Com .NET SDK10 instalado:

```powershell
dotnet build ApolloOverlay.sln -c Release
dotnet tests/ApolloOverlay.Tests/bin/Release/net10.0-windows/ApolloOverlay.Tests.dll artifacts/behavior-report.json
dotnet publish src/ApolloOverlay/ApolloOverlay.csproj -c Release -r win-x64 --self-contained true -o artifacts/minha-publicacao-nova/publish
```

`./scripts/verify.ps1 -OutputVersion native-vN` automatiza os checks e publica em diretório novo. O produtor e seus arquivos são revisados antes da cópia para entrega; não sobrescreva uma publicação revisada.

Compatibilidade com borderless depende do Windows. Fullscreen exclusivo, DX11/12/Vulkan específicos, Bluetooth/controle virtual, HDR, DPI misto, monitores virtuais, Game Bar/Steam Overlay e streaming Apollo/Aurora exigem os testes listados em `docs/pilot-checklist.md`. Ausência de hardware não equivale a aprovação física.
