# Game Session Overlay

Uma overlay para controlar sua sessão de jogo pelo controle, mouse ou teclado, no PC ou em uma TV com streaming. Aplicativo nativo WPF/.NET 10 para Windows 11 x64; funciona com ou sem Apollo.

## Instalar e abrir

Baixe o Setup do canal desejado em [Releases](https://github.com/bezoro/game-session-overlay/releases). A instalação inclui o runtime, os atalhos e o desinstalador. A overlay inicia oculta no logon; abrir ou fechar o painel usa uma transição breve.

Abra pelo botão **Guide/Xbox**, dê **duplo clique no ícone da bandeja** ou escolha **Abrir overlay** no menu da bandeja. Guide usa uma API legada do XInput e continua experimental; a bandeja oferece uma alternativa independente do controle.

O aplicativo não exige administrador para o uso normal. O [guia de uso](<Guia de uso.md>) explica inicialização elevada opcional, escolha de monitor e configuração para a TV.

## Durante o jogo

- Retome a janela da sessão, alterne aplicativos e abra Playnite ou Steam Big Picture.
- Ajuste volume por aplicativo, mixer, microfone e saída de áudio do Windows.
- Aplique perfis TV/Computador ou crie perfis com monitor, escala, posição e áudio.
- Controle mídia compatível, escreva com o teclado virtual e salve capturas locais.
- Veja modelo e bateria do controle quando informados pelo Windows. Metadados ambíguos e cargas desconhecidas permanecem explícitos.
- Navegue com foco visível, resposta ao pressionar, contorno com respiração e reflexo sutil. Sons e vibração são configuráveis.

| Entrada | Navegar | Selecionar | Voltar | Abas |
| --- | --- | --- | --- | --- |
| Controle XInput | Direcional / analógico | A | B | LB / RB |
| Teclado | Setas / Tab | Enter / Espaço | Esc | PageUp / PageDown |
| Mouse | Ponteiro e rolagem | Clique | Botão Voltar | Clique |

X abre as opções da janela pelo controle ou teclado. O fechamento de uma janela exige uma nova pressão mantida por 1,5 segundo; não há encerramento forçado.

**Foco não bloqueia globalmente o controle em outros aplicativos.** Jogos que leem XInput em segundo plano podem continuar recebendo entrada. Em streaming, o dispositivo visível pode ser virtual e não informar a bateria do controle da TV. Consulte [compatibilidade](docs/controller-isolation.md) e o [roteiro de teste real](docs/pilot-checklist.md).

## Atualizações e automação

A versão fica no cabeçalho. Em **Sistema → Versão e atualizações**, escolha canal, consulte mudanças, instale e reinicie ou volte a uma versão preservada. Instalador e atualizações usam Velopack; não exigem login no GitHub.

A instalação inclui `gso.cmd`, uma CLI JSON para terminais e agentes. Consulte [comandos, atualização e retorno](docs/cli.md), [funcionamento do atualizador](docs/updates.md) e [comportamento detalhado](docs/runtime-behavior.md).

## Contribuir

Comece por [CONTRIBUTING.md](CONTRIBUTING.md) e pelo [mapa da arquitetura](docs/architecture.md). O SDK está fixado em [global.json](global.json).

```powershell
dotnet build ApolloOverlay.sln -c Release
./scripts/format.ps1 -Verify
dotnet run --project src/ApolloOverlay -- --preview
```

A solução inclui aplicativo, CLI, testes e ferramentas de diagnóstico. O CI compila com nullable e warnings como erros, verifica formatação, executa testes, gera capturas WPF e valida pacote, instalação, atualização e retorno com Velopack. Fixtures não substituem testes com controle, jogo e TV reais.

Fontes, licenças e proveniência dos vetores: [ícones](docs/third-party-icons.md). Dependências de atualização: [Velopack](docs/third-party-updates.md).
