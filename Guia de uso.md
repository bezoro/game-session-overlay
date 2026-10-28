# Controle da sessão

Abra o painel por cima do jogo para alternar janelas, ajustar o áudio ou acessar suas bibliotecas. Funciona no Windows 11 com controle XInput conectado ao PC e durante uma transmissão pelo Apollo. O Apollo não precisa estar aberto para usar o painel localmente.

## Primeira execução

1. Extraia o pacote para uma pasta permanente. Não execute de dentro do ZIP.
2. Abra **Abrir overlay.cmd**. O painel aparece e o ícone fica na bandeja do Windows, perto do relógio.
3. Em **Sistema**, escolha o monitor, o lado do painel e o tamanho da interface. Se usar Playnite, selecione sua pasta. Pressione **Salvar** para manter as alterações.

Na próxima entrada no Windows, a overlay inicia oculta. Um toque curto no botão **Guide**, o X iluminado no centro do controle Xbox, abre o painel quando você solta o botão. Você também pode dar duplo clique no ícone da bandeja ou escolher **Abrir overlay** no menu do botão direito.

## Usar pelo controle

| Controle | Ação |
| --- | --- |
| Direcional ou analógico esquerdo | Mover a seleção |
| A | Acionar a opção selecionada |
| B | Voltar; na tela Sessão, ocultar o painel |
| LB / RB | Trocar de aba |
| X | Abrir opções da janela em Sessão e Aplicativos |
| Guide | Abrir ou ocultar o painel |
| Esquerda / direita sobre Volume | Ajustar em passos de 5% |

Solte os botões e os analógicos ao voltar ao jogo. O painel espera a soltura para devolver o foco. Se você mover a seleção e apertar A ao mesmo tempo, apenas a seleção muda; aperte A novamente para acionar a opção.

O cartão principal retoma a janela da sessão. Se ela tiver sido fechada, o cartão oferece escolher outra. Nas opções da janela ficam o áudio do aplicativo, minimizar, detalhes e fechamento. Para fechar, selecione **Fechar janela**, solte A e faça uma nova pressão de 1,5 segundo. Soltar antes ou apertar B cancela.

O controle ainda pode agir no jogo enquanto o painel está aberto. A overlay não bloqueia a entrada de outros aplicativos. Pause o jogo antes de navegar quando uma ação involuntária puder atrapalhar sua partida.

## Ajustar para sua TV

Em **Sistema**, comece com o monitor automático. Ele acompanha a janela ativa; ao abrir pela bandeja, tenta usar a janela preservada da sessão. Escolha um monitor específico se quiser manter o painel na tela virtual do Apollo ou em outra tela.

Aumente a escala até conseguir ler os títulos da sua posição habitual. O painel usa rolagem quando falta espaço. A opção de reduzir animações remove as transições; as preferências de animação e alto contraste do Windows também são respeitadas.

Em **Personalizar acesso rápido**, mantenha as ações que você usa e coloque as mais frequentes primeiro. Sons e vibração são opcionais e começam desativados. Os ajustes só persistem depois de **Salvar**; **Descartar** recupera os últimos ajustes salvos.

Em **Sistema → Sons**, ative os sinais e use **Testar retorno** para ouvir uma amostra. Navegação, confirmação e retorno têm sons diferentes. Uma falha usa um sinal grave; o som de conclusão só toca quando o resultado foi confirmado. O teste solicita a reprodução, mas você precisa conferir se a saída de áudio está chegando à TV.

A opção selecionada mantém o contorno de foco. Ao acionar uma função, o botão recebe um destaque breve, inclusive com animações reduzidas. Resultados também usam texto e símbolos para distinguir espera, conclusão e problemas.

**Áudio do Windows** controla a saída padrão do PC. **Áudio do aplicativo**, nas opções da janela, afeta as sessões de áudio identificadas daquele aplicativo. Nenhum deles garante alterar o volume físico da TV.

## Quando algo não funcionar

| Situação | O que tentar |
| --- | --- |
| Guide não abre o painel | Abra pela bandeja, confira Guide em Sistema e faça um toque curto, soltando dentro do limite configurado. A leitura de Guide é experimental. |
| Steam ou Game Bar abre junto | Desative a abertura pelo botão Guide nas configurações desses aplicativos. |
| Painel não recebe comandos | Solte os botões e abra novamente pela bandeja para pedir foco ao Windows. |
| Painel não aparece sobre o jogo | Teste o jogo em janela sem bordas. Tela cheia exclusiva não tem compatibilidade garantida. |
| Painel aparece na tela errada | Escolha o monitor em Sistema e salve. Confira novamente após conectar ou remover telas. |
| Playnite não aparece | Em Sistema, escolha a unidade e as pastas até encontrar a instalação. Selecione **Usar Playnite desta pasta** e salve. |
| Resultado de uma ação ficou incerto | Use **Ler** para ver a mensagem completa e confira a janela antes de repetir a ação. |

Você pode usar setas, Tab, Enter e Esc como alternativa ao controle. Para encerrar a overlay, abra o menu do ícone da bandeja e escolha **Sair**.

## Inicialização e remoção

A inicialização comum não exige administrador. Você pode desativá-la em **Configurações do Windows → Aplicativos → Inicialização**. Se mover a pasta do aplicativo, abra a overlay uma vez no novo local para atualizar a entrada existente.

Para instalar a inicialização elevada, abra PowerShell como administrador na pasta do pacote e execute `./startup-task.ps1 -Action Install`. Use a conta que vai utilizar a overlay. A instalação exige confirmação do Windows e só vale para os próximos logons. Para remover essa tarefa, execute `./startup-task.ps1 -Action Uninstall` também como administrador. A elevação não isola o controle do jogo.

Para remover o aplicativo, encerre pela bandeja, desative a inicialização ou remova a tarefa elevada e exclua a pasta do pacote. Seus ajustes e logs permanecem em `%LOCALAPPDATA%\Bezoro\GameSessionOverlay` até você excluí-los.

Os limites conhecidos estão em [compatibilidade](docs/pilot-checklist.md) e [isolamento do controle](docs/controller-isolation.md). Informações de compilação e diagnóstico estão no [README](README.md).
