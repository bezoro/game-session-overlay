# Compatibilidade real pendente

Use **Abrir overlay.cmd**. O produto opera normalmente; não há flag de piloto ou confirmação de entrada.

- Controle USB/Bluetooth/virtual: abrir por LB+RB+Start650ms, soltar, navegar em todas as páginas, desconectar/reconectar, retornar com B. Verificar conflito do chord e observar se o jogo também recebe os botões.
- Janelas borderless DX11/DX12/Vulkan: abertura, alt-tab, retorno, recusa de foco e troca de sessão. Fullscreen exclusivo não é garantido.
- Steam/Playnite presentes e ausentes; Big Picture, atalhos e abertura/reuso. Só considerar visibilidade confirmada se realmente observada. Guide longo e preservação do toque curto seguem indisponíveis.
- Dois monitores/DPI misto, origem negativa, monitor virtual Apollo, resolução removida/trocada, 4K100% na TV; assegurar texto e foco legíveis.
- Claro/escuro enquanto o painel já está aberto, alto contraste, animações do Windows desativadas e HDR. Não alterar configurações do usuário durante checks automáticos.
- Áudio: mudança do endpoint durante página aberta, desconexão, volume/mudo por outro aplicativo. Confirmar atualização sem reiniciar.
- Fechar somente uma janela de teste antes de experimentar um jogo: pressão nova1,5s, cancelar, trabalho não salvo e recusa. Nunca forçar encerramento.
- Stream Apollo/Sunshine → Moonlight/Aurora/TV: verificar captura da janela, input e Game Bar; compatibilidade depende da sessão e não é inferida pela presença de monitor virtual.

Checks automáticos usam janelas próprias e dados de contexto reais dessas janelas. Capturas offscreen demonstram layout, não compatibilidade de hardware. Medições partem de comandos de software e não medem botão físico ou latência de rede.
