# Bezoro · Controle da sessão

Overlay nativa WPF/.NET 10 para Windows 11. Não requer administrador, SDK nem instalação no Windows quando executada pelo pacote self-contained.

No pacote, dê duplo clique em **Abrir overlay.cmd**. `ApolloOverlay.exe` inicia na bandeja; clique no ícone para abrir ou use **Ctrl+Alt+Espaço**. Botão direito na bandeja oferece Abrir, Sistema e Encerrar. `ApolloOverlay.exe --show` abre imediatamente. Nenhuma inicialização automática é registrada.

- Controle: segure **LB+RB+Start por650ms** para abrir/ocultar; solte a combinação antes de selecionar. Direcional/analógico navega, A seleciona, B retorna, X mostra detalhes, LB/RB trocam abas.
- Teclado: setas, Tab/Shift+Tab, Enter/Espaço, Esc, X e PageUp/PageDown. O teclado consegue alcançar todas as opções.
- Fechar janela: solte e mantenha uma nova pressão de A/Enter por1,5s. Soltar, B, desconectar ou perder foco cancela. Steam, Playnite, Explorer e janelas técnicas são protegidos. Não há encerramento forçado.
- Áudio: controla a saída padrão do Windows, incluindo volume e mudo; não garante volume físico da TV nem endpoint do streaming.
- Playnite: descoberta local ou caminho pelo Sistema. Steam: protocolo público Big Picture. Atalhos de Game Bar/Steam/Visão de tarefas são solicitações; visibilidade e controle externo não são presumidos.

A interface acompanha automaticamente claro/escuro e alto contraste do Windows por recursos Fluent dinâmicos. Há animações breves e opção de redução. Sem capas inventadas, captura contínua, injeção em jogos, driver ou telemetria.

**Guide/Xbox é opt-in experimental:** `GuideXInputEnabled:true` no JSON (padrão `false`) habilita a leitura legada não documentada do XInput; um toque abre ou fecha a overlay, sem ação especial de pressão longa. A abertura e o fechamento foram confirmados em teste com Playnite; o fechamento sobre Steam Big Picture exige reteste desta correção. Atalho e combinação continuam disponíveis se a leitura falhar. A overlay não bloqueia Guide em outros programas: os atalhos Guide da Steam e as opções de controle da Game Bar precisam permanecer desativados para evitar ações concorrentes; Win+G e Win+Tab continuam disponíveis. Foco da janela não isola XInput: o jogo pode receber os mesmos botões. Prefira uma combinação que não conflite com o jogo.

Ajustes são explicitamente salvos em `%LOCALAPPDATA%\Bezoro\GameSessionOverlay\settings.json`; configuração anterior e fonte corrompida são preservadas. Sistema permite mudar atalho, combinação, deadzone, monitor, animações e caminhos. JSON avançado aceita `Version:1`, `Hotkey` com modificadores+letra/Space, `Chord` com pelo menos dois botões comuns, `ChordHoldMs`300–2000 e `Deadzone`8000–28000. Não use Guide na combinação. Logs locais rotativos limitam-se a três arquivos de aproximadamente512KiB, sem títulos/caminhos privados.

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
