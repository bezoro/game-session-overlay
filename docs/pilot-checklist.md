# Compatibilidade real pendente

Use **Abrir overlay.cmd**. O produto opera normalmente; não há flag de piloto ou confirmação de entrada.

- Controle USB/Bluetooth/virtual: verificar que o Guide experimental está ligado nas configurações. Após uma leitura neutra com Guide solto, pressionar brevemente e soltar dentro do limite configurado para abrir; depois navegar em todas as páginas, desconectar/reconectar e retornar com B. Se o Guide não estiver disponível, abrir pelo menu da bandeja. Observar se o jogo ou outro aplicativo também recebe o Guide; a leitura não bloqueia o botão para outros processos.
- Janelas borderless DX11/DX12/Vulkan: abertura, alt-tab, retorno, recusa de foco e troca de sessão. Fullscreen exclusivo não é garantido.
- Steam/Playnite presentes e ausentes; Big Picture, atalhos e abertura/reuso. Só considerar visibilidade confirmada se realmente observada. Confirmar que uma pressão de Guide acima do limite não abre o painel ao soltar. O toque curto pode também acionar Steam/Game Bar; a compatibilidade física e o comportamento de pressão longa/desligamento precisam de validação manual.
- Dois monitores/DPI misto, origem negativa, monitor virtual Apollo, resolução removida/trocada, 4K100% na TV; assegurar texto e foco legíveis.
- Claro/escuro enquanto o painel já está aberto, alto contraste, animações do Windows desativadas e HDR. Não alterar configurações do usuário durante checks automáticos.
- Áudio: mudança do endpoint durante página aberta, desconexão, volume/mudo por outro aplicativo. Confirmar atualização sem reiniciar.
- Fechar somente uma janela de teste antes de experimentar um jogo: pressão nova1,5s, cancelar, trabalho não salvo e recusa. Nunca forçar encerramento.
- Stream Apollo/Sunshine → Moonlight/Aurora/TV: verificar captura da janela, input e Game Bar; compatibilidade depende da sessão e não é inferida pela presença de monitor virtual.

Checks automáticos usam janelas próprias e dados de contexto reais dessas janelas. Capturas offscreen demonstram layout, não compatibilidade de hardware. Medições partem de comandos de software e não medem botão físico ou latência de rede.


## Painel da sessão: validação complementar

- Conferir cartão por mouse/A/Enter e opções por botão irmão/Tab/direita/X. Ler título longo completo em Detalhes; confirmar foco real e leitura com setas até o fim.
- Em aplicativo próprio de teste, comparar som ativo/mudo/misto e múltiplas saídas. O comando afeta o aplicativo e suas janelas, nunca o volume global como alternativa. Conferir perda de dispositivo, alteração externa e resultado parcial; não confundir envio com confirmação.
- Minimizar somente janela própria, observar o estado minimizado e manter o painel aberto. Retorno anterior/recentes só devem promover mudanças de primeiro plano confirmadas, sem gravação do histórico.
- Reordenar e ocultar/restaurar todas as ações; testar escala100/115/130/150%, ambos os lados, DPI/monitores/área útil e seleção revelada. Verificar360×480 com ajustes pendentes e resultado retido: Salvar/Descartar fixos e leitura/dispensa alcançáveis na rolagem.
- Percorrer os três passos opcionais, Back/Esc/Pular/Concluir, preservar rascunho e salvar apenas explicitamente. Conferir recuperação v1/v2→v3 e lista de favoritos vazia.
- Com autorização explícita para hardware, verificar slot ativo, reconexão, bateria categórica/com fio/desconhecida, ausência de consulta oculta e consulta visível no máximo a cada5s. Sons/vibração começam desligados; verificar sinais deliberados, teste opt-in e parada ao perder foco/ocultar/desconectar. Resultado XInput aceito não prova percepção nem isolamento contra outro processo.

Os testes de serviços usam catálogos/COM/vibração/áudio sintéticos. Testes de foco e leitura usam apenas janelas próprias fora da área visível; capturas renderizam somente a árvore visual própria. Isso não estabelece compatibilidade com hardware, DPI físico, áudio externo ou posição nativa em todos os monitores.

