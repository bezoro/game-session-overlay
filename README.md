# Apollo · painel da TV

Prévia nativa em português para Windows x64, com piloto experimental explícito. Abra `Iniciar-previa.cmd` ou `artifacts/publish/ApolloOverlay.exe`. A execução padrão é sempre **prévia segura**: janelas fictícias, ações simuladas, sem atalhos globais, Guide, envio de teclas ou inicialização de programas.

Requer .NET Desktop Runtime 10 x64 (não incluído). O ZIP portátil contém o executável, DLL, arquivos de runtime, documentação e atalhos. Extraia todos os arquivos juntos. Nenhuma instalação, inicialização automática ou elevação é feita.

Use D-pad / analógico esquerdo / setas para navegar, A / Enter para escolher, B / Esc para voltar, LB/RB / PageUp/PageDown para trocar de página e X para detalhes da janela selecionada. Tab e Shift+Tab também navegam. Há três páginas: Início, Aplicativos e Ajustes. Para fechar uma janela, solte A/Enter e pressione novamente por 1,5 s; B/Esc cancela. Somente WM_CLOSE é solicitado, sem matar processos. Diálogos de salvar e recusas podem deixar o fechamento não confirmado.

## Piloto controlado

Após a revisão do candidato, execute `Iniciar-teste.cmd` ou `ApolloOverlay.exe --pilot`. Leia a advertência e selecione **Iniciar teste controlado**. O jogo pode continuar recebendo o controle: foco e topmost não isolam XInput. Use ambiente sem trabalho não salvo e mantenha um teclado disponível. Siga [o checklist](docs/pilot-checklist.md) (no ZIP, `pilot-checklist.md`). Não use ainda como controle exclusivo de um jogo arbitrário.

Guide usa somente GameInput v2 já existente em System32. Não instala runtime e não usa bits/ordinais XInput não documentados. Runtime ausente, interface incompatível ou falha de inscrição aparecem como indisponível. Inscrição não significa que o Guide chegou pelo Apollo/Aurora; só um evento real muda essa evidência. Ctrl+Alt+Espaço é a alternativa registrada apenas após iniciar o piloto; colisão é informada. É possível que outro aplicativo consuma Guide ou tenha exclusividade. Reabra o painel com Guide/atalho; para sair, vá a Ajustes → Encerrar painel, ou Alt+F4 com o próprio painel focado.

O primeiro comando real captura a janela externa revelada antes de o painel retornar. Launchers Playnite/Steam e o próprio painel não substituem a janela da sessão. Aplicativos → X permite selecionar explicitamente o alvo. Confira o título sempre. A identidade inclui HWND, PID, início do processo e geração de destruição observada. EVENT_OBJECT_DESTROY invalida substituições observadas dentro do mesmo processo; há uma corrida inevitável entre observação assíncrona, verificação e envio ao HWND. Se o hook não for criado, as ações de janela são bloqueadas. Não há garantia absoluta contra reutilização instantânea antes da entrega do evento.

Playnite exige `Playnite.FullscreenApp.exe`; a descoberta é limitada à instalação padrão em LocalAppData. Ajustes permite escolher outro caminho com teclado. O argumento é `--startfullscreen`. Steam usa `steam://open/bigpicture`; o cliente pode atualizar/reiniciar, e o envio da URI não comprova abertura em tela cheia. Não é necessário percorrer a árvore de processos para reconhecer a janela real.

A tela principal do Windows é o ponto de partida; Ajustes percorre as telas e mudanças de topologia atualizam a posição com DPI por monitor. O painel não sabe qual tela Apollo está transmitindo e não configura monitor virtual. Jogos em tela cheia exclusiva podem cobri-lo. Apenas modo desktop/sem bordas está no escopo do piloto.

Visão de tarefas e Game Bar solicitam Win+Tab e Win+G sobre o alvo revalidado. Não comprovam abertura. Ao sair para uma interface externa, o painel para de navegar por A/B/D-pad até ser reaberto. **Navegação e fechamento externos de Game Bar/Steam pelo controle não estão implementados/verificados.** Os respectivos comandos de fechar ficam indisponíveis. Não há conversão silenciosa de controle em teclas externas.

O teste do overlay Steam só é habilitado quando o módulo conhecido está carregado no processo alvo. Steam aberto, origem do jogo ou encadeamento Playnite → Steam não bastam. Acesso negado ou módulo ausente deixam o recurso indisponível. Atalhos de jogos não Steam também podem funcionar quando o módulo existe. Em Ajustes → Integrações, teste, observe a resposta e reabra o painel; só então confirme manualmente. A confirmação é específica ao processo/janela/atalho e é invalidada quando mudam. Alternar não significa Fechar: visibilidade continua desconhecida.

Ajustes são gravados **somente** ao selecionar Salvar ajustes no piloto, em `data/settings.json` ao lado do executável. Há substituição atômica e recuperação pela cópia `.previous`. Nenhuma fila de ações ou estado externo é persistido; reiniciar sem argumentos sempre volta à prévia. Títulos/caminhos privados não são registrados por padrão. Não há volume, force kill, suspensão/injeção de jogos ou mudanças em configurações de Apollo/Aurora/Steam/Game Bar nesta versão.

## Verificação local

Com PowerShell 7 e SDK .NET 10.0.402: `pwsh -NoProfile -File scripts/verify.ps1 -OutputVersion v1`. O script restaura apenas da pasta local (sem pacotes externos), compila Release, executa o harness determinístico, consulta inicialização v2 sem callback, renderiza quatro estados reais WPF sem mostrar janela e publica sem recompilar. Relatórios e capturas ficam em `artifacts/check-v1`. A validação não inicia o piloto nem opera aplicativos externos. A suíte não substitui o teste real com TV, controle, Apollo e Aurora.
