# Versões e atualizações

A versão exibida pela overlay identifica o executável em uso. Ela vem da compilação, não do nome da pasta. Em compilações publicadas, inclui o número da execução do CI e o commit de origem.

Exemplos:

- `0.2.0-preview.42.1+abc1234`: versão de testes, execução 42, tentativa 1, commit `abc1234`.
- `0.2.42+abc1234.a1`: versão estável da mesma execução.
- `0.2.0-dev+local`: compilação local sem identificação de uma publicação.

Cada pacote também possui um identificador único, como `ci-42-a1-abc1234`. Uma nova tentativa do CI recebe outro identificador. Pacotes publicados não são substituídos por outros arquivos com o mesmo identificador.

## Publicação pelo repositório

O workflow `Windows checks` compila, executa as verificações de comportamento e da CLI, gera capturas e empacota a aplicação. Um segundo job publica a versão somente quando o primeiro job inteiro termina com sucesso.

Antes da publicação, o workflow também instala o pacote em uma pasta isolada do runner e inicia o executável publicado. O teste verifica a confirmação de inicialização, uma segunda chamada ao inicializador e a recuperação da versão anterior após rejeitar uma identificação inconsistente. A comparação dos ajustes e dos valores de inicialização do usuário faz parte dessa verificação.

Os canais são:

| Canal | Origem | Publicação |
| --- | --- | --- |
| Estável | Push em `main` | Release normal |
| Testes | Push em `codex/controller-overlay-polish` | Prerelease |

Pull requests compilam e executam testes com permissões de leitura. Eles não publicam versões. Alterações em outras branches também não entram automaticamente nesses canais. Assim, o botão de atualização oferece compilações que passaram pelas verificações do canal escolhido.

As publicações usam GitHub Releases, com dois arquivos:

- `GameSessionOverlay-<buildId>-win-x64.zip`, contendo a aplicação e sua identificação.
- `overlay-release.json`, contendo versão, identificador, commit, canal, data e nome, tamanho e SHA-256 do pacote.

O pacote contém `build-info.json` na raiz. O empacotador compara essa identificação com os metadados da DLL compilada. Antes da publicação, o job confere novamente o hash do ZIP e a correspondência entre as duas identificações.

O GitHub recebe primeiro uma release em rascunho. Ela só fica disponível ao aplicativo depois do envio e da verificação dos dois arquivos. Releases anteriores são preservadas. Os artefatos temporários do CI continuam úteis para diagnóstico, mas não são o catálogo de atualizações.

## Instalação e retorno a versões anteriores

Para a primeira instalação, extraia o pacote e execute `Instalar overlay.cmd`. A instalação gerenciada mantém um inicializador permanente e pastas separadas para cada versão. Atalhos e inicialização automática apontam para o inicializador, permitindo trocar a versão sem sobrescrever arquivos em execução.

Os ajustes do usuário ficam separados dos executáveis. O fluxo de atualização guarda o estado necessário para retornar à versão anterior. Uma falha no download ou na verificação do pacote não deve substituir a versão atual.

Após a troca, a nova instância precisa confirmar que iniciou. Se ela não confirmar dentro do prazo, o inicializador recupera a versão anterior. Essa confirmação cobre a inicialização da aplicação; o funcionamento do controle, da TV e do streaming ainda precisa ser validado no uso real.

O histórico de versões permite escolher uma versão anterior. Uma cópia já instalada pode ser usada sem baixar novamente seu pacote. Se a versão desejada só estiver no GitHub, o acesso ao repositório e à rede será necessário.

Repositórios privados exigem autenticação do usuário no aplicativo. A autorização desta conversa e a conexão do Codex com o GitHub não são transferidas para um executável instalado no Windows. Nenhuma credencial é incorporada ao pacote ou ao manifesto de atualização.

## Compilar uma versão identificada

O CI define os mesmos quatro valores para compilação e empacotamento:

```powershell
$env:OVERLAY_VERSION = '0.2.0-preview.42.1+abc1234'
$env:OVERLAY_BUILD_ID = 'ci-42-a1-abc1234'
$env:OVERLAY_COMMIT = 'abc1234000000000000000000000000000000000'
$env:OVERLAY_CHANNEL = 'testing'
./scripts/verify.ps1 -Stage Build -OutputVersion native-v42
./scripts/verify.ps1 -Stage Package -OutputVersion native-v42
```

Use o hash real e completo do commit. O exemplo só mostra o formato. Os quatro valores precisam corresponder à DLL publicada; caso contrário, o empacotamento falha. Um pacote existente também não é sobrescrito. Para outra compilação, use outro identificador e um diretório de saída novo.

`scripts/test-release-pipeline.ps1` verifica os scripts, a correspondência dos metadados compilados, a integridade do ZIP e a recusa de sobrescrita ou identidade inválida. Os testes do publicador usam respostas simuladas do GitHub e não publicam releases reais.
