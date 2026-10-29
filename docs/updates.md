# Versões e atualizações

A instalação e a troca de executáveis usam [Velopack 1.2.161](https://github.com/velopack/velopack/releases/tag/1.2.161). O aplicativo WPF continua responsável pela navegação, pelo progresso e pelo histórico apresentados na overlay. O pacote .NET `Velopack` e a ferramenta oficial `vpk` estão fixados na mesma versão.

## Instalar e atualizar

Baixe o arquivo `GameSessionOverlay-testing-Setup.exe` ou `GameSessionOverlay-stable-Setup.exe` nas [releases do projeto](https://github.com/bezoro/game-session-overlay/releases) e execute-o. O Setup instala a aplicação e cria sua entrada no menu Iniciar e a opção de desinstalação do Windows. A instalação padrão é por usuário.

Abra **Sistema → Atualizações** para conferir a versão, procurar atualizações ou escolher uma versão anterior. A nova versão é baixada antes de solicitar o fechamento da overlay. O mecanismo oficial do Velopack aplica o pacote depois que o processo termina e reinicia o aplicativo.

Os canais são:

| Canal | Origem | Publicação |
| --- | --- | --- |
| Estável | Push em `main` | Release normal |
| Testes | Execução manual de Windows checks na `main`, com `channel: testing` | Prerelease |

Somente compilações aprovadas nas verificações do Windows são publicadas. Pull requests executam as verificações com permissões de leitura e não publicam versões. O repositório é identificado também pelo ID `1430327913`, que continua válido após uma mudança de nome.

A opção manual `validate` executa as verificações sem publicar. O canal de testes usa a `main` já integrada; executar o workflow em outra branch nunca publica uma versão.

## Identificar a versão

A versão exibida vem dos metadados compilados do executável. GitVersion 6.8.2 calcula `MAJOR.MINOR.PATCH` pelos commits e tags; o CI acrescenta o rótulo de prévia e o commit. Exemplos:

- `0.48.0-preview.55.1+abc1234`: prévia de 0.48.0, execução 55, tentativa 1.
- `0.48.0+abc1234`: versão estável; o número da execução não altera seu significado.
- `0.2.0-dev+local`: build local sem identidade de publicação.

O identificador `ci-55-a1-abc1234` rastreia a execução separadamente da versão. O formato NuGet do Velopack usa a parte anterior ao `+`; o commit continua no executável e nos metadados. Tags antigas, inclusive `0.47.1`, permanecem válidas e estabelecem a ordem da migração.

`feat` avança MINOR; `fix`, `perf`, `refactor`, `build` e `revert` avançam PATCH. `!` ou `BREAKING CHANGE:` avançam MAJOR. `docs`, `style`, `test`, `ci` e `chore` não criam uma release estável por si só. A [convenção para contribuidores](../CONTRIBUTING.md#commits-e-versões) explica os títulos e exemplos.

Uma tag reserva a versão; o CI não reconstrói uma release estável existente com outros bytes. Para repetir uma publicação que falhou, reexecute somente o job **Publish validated update**, reutilizando o artefato original aprovado. Prévias recebem outro identificador NuGet em cada tentativa. Uma prévia solicitada a partir de uma versão estável já reservada usa o próximo PATCH, para continuar sendo mais nova para o atualizador.

## Voltar a uma versão anterior

O histórico mantém cópias verificadas dos pacotes já utilizados, separadas do cache temporário do Velopack. Isso permite voltar a uma cópia preservada mesmo sem baixar novamente seu pacote. Versões antigas publicadas no GitHub também podem ser selecionadas quando houver conexão.

O aplicativo guarda seus ajustes fora das pastas dos executáveis. A política de reversão usa os backups associados à versão escolhida e preserva uma cópia dos ajustes mais recentes. A instalação e a aplicação do pacote continuam sendo feitas pelo Velopack, inclusive na reversão.

Uma falha de download ou de integridade impede a aplicação daquele pacote. A aplicação não declara que uma atualização funcionou apenas porque foi baixada. Não há promessa de reversão automática baseada no comportamento do jogo ou do controle; a opção de voltar fica disponível na interface.

## Empacotamento e publicação

`dotnet tool restore` instala a versão do `vpk` registrada em `.config/dotnet-tools.json`. O workflow publica a aplicação como `win-x64` com o runtime .NET incluído e chama o empacotador oficial:

```powershell
dotnet tool run vpk -- pack --packId GameSessionOverlay --packVersion VERSION --packDir PUBLISH --mainExe ApolloOverlay.exe --packTitle "Game Session Overlay" --channel testing --runtime win-x64 --outputDir RELEASES --noPortable --delta None --skip-updates
```

Os arquivos de atualização são os formatos do próprio Velopack:

- `GameSessionOverlay-<version>-<channel>-full.nupkg`.
- `GameSessionOverlay-<channel>-Setup.exe`.
- `releases.<channel>.json`, com o índice e os hashes dos pacotes.
- `assets.<channel>.json`, usado pela CLI oficial para publicar os arquivos daquela compilação.

O arquivo `build-info.json` registra a identificação compilada dentro do aplicativo. Ele serve para conferir a procedência da versão; não substitui o índice de atualização do Velopack. As notas da versão também contêm as linhas `Version:`, `Build:`, `Commit:` e `Channel:` para a apresentação dessa identificação na overlay.

A publicação usa `vpk upload github`, com uma credencial temporária do job fornecida pela variável `VPK_TOKEN`. O token não entra nos argumentos da linha de comando, no aplicativo ou nos pacotes. A CLI cria a release em rascunho, envia os arquivos e só então a publica. Releases anteriores não são removidas automaticamente.

O status `overlay/update-release` no commit aponta para a execução que publicou a versão e informa se a entrega terminou. A identidade estável do repositório é conferida antes da publicação. O projeto é público, portanto a consulta e o download no aplicativo não exigem um token pessoal do GitHub.

## Verificações

Além das suítes WPF e da CLI, o pipeline confere os arquivos produzidos pelo Velopack: versão e ID do aplicativo, canal, hashes, inventário de publicação, especificação NuGet e metadados compilados do executável embalado.

Uma prova adicional, restrita ao runner Windows, executa o Setup oficial em uma pasta isolada e inicia a WPF instalada A. Ela compila uma WPF B real com identificação e binário distintos, aplica B com o `Update.exe` oficial aguardando o PID exato de A e inicia B pelo inicializador do Velopack. O retorno usa a CLI instalada: `gso rollback BUILD_ID` prepara A pelo histórico local e passa pelo fluxo de aplicação da própria overlay, incluindo `ScheduleApplyAsync` e o reinício pelo SDK. Em A, B e A restaurada, uma nova consulta da CLI confere a identidade da WPF em execução. O teste também verifica a versão instalada, o hash da DLL, os pacotes preservados no histórico e os ajustes; depois usa o desinstalador oficial e confere os arquivos e a inicialização do usuário. Essa prova não automatiza o botão da interface nem comprova recuperação automática após falha de saúde. Os pacotes B ficam apenas em pastas temporárias e nunca são publicados. Os registros da prova ficam nos artefatos do CI. O funcionamento físico do Guide, do controle, da TV e do Apollo continua exigindo teste no equipamento real.

A licença do atualizador está em [dependências de atualização](third-party-updates.md).
