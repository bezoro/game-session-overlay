# Painel da sessão

Esta evolução parte da foto da versão 32: o jogo precisa identificar a sessão, e áudio, microfone e tarefas frequentes devem aparecer antes dos atalhos de bibliotecas. A navegação continua disponível por controle, mouse e teclado.

## Direção visual

- Obsidiana `#15191F`: superfície opaca, legível sobre jogos claros.
- Porcelana `#F2F3F5`: títulos e ações.
- Aço `#A5B0BD`: descrições secundárias.
- Névoa `#92BDEA`: seleção, com contorno duplo e preenchimento discreto.
- Cobre `#C89A70`: identidade da sessão, usado com moderação.
- Verde `#78C999`: resultado confirmado, distinto da seleção.

Segoe UI Variable Display nos títulos e Segoe UI Variable Text nos controles. Alinhamento à esquerda, linhas curtas, números de volume alinhados e controles com tamanho consistente. O tema claro recebe equivalentes com contraste; alto contraste conserva as cores do sistema. A seleção respira no contorno e recebe um reflexo de baixa opacidade; o clique comprime brevemente o controle. Efeitos param ao ocultar, descarregar ou perder foco. A preferência antiga de reduzir movimento é apenas lida para compatibilidade e não participa mais da interface. Sons e vibração seguem as preferências existentes.

```text
Game Session                       v32   Ajustes
Sessão             Aplicativos           Áudio

Imagem real / ícone    Nome do jogo
                      Retomar       Opções

Jogo                   volume / sem som
Saída do Windows       volume / sem som
Microfone              estado explícito

Perfil TV / Computador     Capturar tela
Mídia                     Teclado

Bibliotecas e atalhos secundários

Entrada compartilhada       instruções contextuais
```

A imagem da sessão é o único elemento expressivo. Os controles permanecem discretos; os efeitos contínuos ficam restritos ao item selecionado, com respiração de três segundos e um reflexo a cada cinco segundos. Imagens vêm de ícones reais ou capturas locais, com fallback legível. A versão curta permanece visível; build e commit continuam acessíveis nos detalhes.

## Entregas funcionais

1. Volumes independentes das sessões identificadas, microfone padrão e seleção explícita da saída do Windows. O dispositivo alterado aparece pelo nome. A overlay não afirma controlar o volume físico da TV.
2. Perfis TV e Computador, salvos explicitamente, com monitor da overlay, escala e áudio. Ajustes pendentes bloqueiam aplicação; falhas parciais e mudanças externas não são sobrescritas por uma restauração cega.
3. Mídia pelos controles do Windows nos aplicativos compatíveis, com estado observado e comandos indisponíveis explicados.
4. Teclado virtual para controle, edição local e inserção deliberada de texto em uma janela identificada. Inserir texto não pressiona Enter automaticamente. Foco, soltura e identidade continuam obrigatórios.
5. Captura deliberada do monitor e galeria local, confirmando arquivo salvo. Nenhuma gravação ou captura contínua. Imagens protegidas e tela cheia exclusiva não recebem promessas de compatibilidade universal.

O estado de entrada compartilhada continua explícito. Foco não comprova bloqueio global de XInput; esta evolução não anuncia isolamento sem integração e observação que o demonstrem.

## Verificação

Exercitar serviços com interfaces simuladas sem alterar áudio, mídia, entrada ou tela do runner. Os testes da interface precisam observar foco real, estados disponíveis/indisponíveis, seleção após reconstrução, cancelamento e perfis em ambos os canais. Gerar capturas dos temas e dos layouts TV/compacto, inspecioná-las e medir custo quando oculto. A publicação depende dos checks Windows e de review do Codex sem apontamentos pendentes.

O indicador do controle fica no topo, com silhueta por família e bateria. A identificação usa metadados oficiais de Windows.Gaming.Input; o roteamento de entrada continua independente. Não se associa um modelo ao slot quando há múltiplos dispositivos possíveis. Uma porcentagem exige capacidades válidas da bateria; XInput categórico não é convertido em porcentagem. A consulta ocorre somente com o painel visível e confirmado, no máximo a cada cinco segundos.
