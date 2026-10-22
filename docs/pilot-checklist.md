# Teste controlado — ainda pendente

Execute somente após a revisão do pacote. Registre versões Apollo/Aurora, TV, controle, pareamento (TV ou PC), runtime e tela transmitida. Use uma janela descartável primeiro, depois um jogo cooperativo sem trabalho não salvo. Tenha teclado para Ctrl+Alt+Espaço e Alt+F4 no painel. Não execute em um jogo que possa causar dano ao receber entradas involuntárias.

1. Abra a prévia e percorra todas as páginas com controle/teclado. Confira foco, detalhes X, título longo, cancelamento B e confirmação com uma nova pressão de A por 1,5 s.
2. Inicie `--pilot` e leia a advertência. Só **Iniciar teste controlado** ativa adaptadores reais. Confira a janela da sessão e a tela. Se estiver errada, escolha em Aplicativos/Ajustes.
3. Verifique que Guide chega uma vez por pressão e que soltar/reconectar não ativa ações. Inscrição GameInput não é evidência de entrega. Teste conflitos Game Bar/Steam e Ctrl+Alt+Espaço. Não altere configurações desses aplicativos automaticamente.
4. Teste se o jogo continua recebendo D-pad/A/B enquanto o painel está aberto, incluindo um jogo que leia XInput independentemente do foco. **Se houver entrada indesejada, pare o piloto.** Isolamento ainda requer outra solução e aceitação separada.
5. Com uma janela descartável, teste alternar e fechar; confira título e PID, cancele, depois segure A. Teste diálogo de salvar, recusa, janela encerrada/reaberta e outro aplicativo em primeiro plano. Nunca force encerramento.
6. Teste Playnite → Steam Big Picture → jogo. Confira que launchers não viram jogo e o alvo real é identificado quando observado. Solicitação não comprova modo de tela nem foco.
7. Teste overlay Steam ativado, desativado, acesso negado e atalho não Steam. Só confirme após observar o teste no alvo correto. Trocar janela/processo/atalho deve invalidar confirmação.
8. Teste Visão de tarefas/Game Bar/Steam apenas com teclado de recuperação. Navegação e fechamento por controle permanecem pendentes; o painel suspende navegação ao passar a uma interface externa. Pare se Guide/atalho não reabrir. Não trate um segundo toggle como fechar garantido.
9. Confira monitor virtual transmitido, seleção manual, 720p/1080p/4K, DPI, origem negativa e remoção da tela. Não altere drivers ou configurações de captura. Teste sem bordas; tela cheia exclusiva pode cobrir o painel.
10. Encerre em Ajustes. Verifique que atalhos/callbacks foram removidos e que um reinício normal abre a prévia. Falha de remoção Guide mantém runtime/contexto vivos até o processo sair, evitando uso após liberação. Salvar ajustes é uma ação separada, nunca automática.

Registre resultados como solicitado, observado, falhou ou desconhecido; inclua alvo exato e recuperação. Uma captura da interface ou teste simulado não comprova o percurso TV → Aurora → Apollo → jogo, isolamento, disponibilidade Guide, abertura nem fechamento externos.
