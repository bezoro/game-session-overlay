# Desenvolvimento

O aplicativo é WPF/.NET 10 para Windows 11 x64. Instale o SDK indicado em [global.json](global.json) e PowerShell 7. O instalador publicado inclui o runtime; o SDK é necessário apenas para desenvolver.

Comece por [arquitetura](docs/architecture.md), que mapeia os módulos, a propriedade dos recursos nativos e o fluxo de uma ação. [README](README.md) descreve o comportamento do produto; [docs/cli.md](docs/cli.md), a automação.

```powershell
dotnet build ApolloOverlay.sln -c Release
./scripts/format.ps1 -Verify
dotnet tests/ApolloOverlay.Tests/bin/Release/net10.0-windows10.0.19041.0/ApolloOverlay.Tests.dll artifacts/local/behavior.json
dotnet tests/ApolloOverlay.Cli.Tests/bin/Release/net10.0-windows/ApolloOverlay.Cli.Tests.dll
```

A solução compila também GuideProbe e NativeProof, ferramentas de diagnóstico separadas do produto. Seus testes sintéticos estão no CI; modos que consultam hardware ou operam janelas são executados explicitamente, conforme os respectivos READMEs.

Para explorar a interface sem acionar outros aplicativos:

```powershell
dotnet run --project src/ApolloOverlay -- --preview
```

Para testar ações reais, use o instalador e o [roteiro de teste](docs/pilot-checklist.md). Fixtures e capturas WPF não comprovam compatibilidade com um controle físico, uma TV ou um jogo específico.

## Alterações

- Coloque regras e acesso ao sistema nos módulos correspondentes. A interface coordena serviços e apresenta snapshots; serviços não procuram controles WPF nem dependem de `MainWindow`.
- Use os contratos existentes para limites que exigem substituição em testes: áudio, plataformas, controle, mídia e atualizações. Não crie interfaces para cálculos locais sem necessidade de substituição.
- Preserve cancelamento, identidade do alvo e verificações imediatamente antes de efeitos externos. Uma solicitação enviada não significa uma ação concluída; o resultado deve refletir o que foi observado.
- Mantenha consultas e callbacks fora do dispatcher quando puderem bloquear. Publique snapshots imutáveis, rejeite gerações antigas e documente quem libera cada recurso nativo.
- Teste o comportamento afetado, incluindo foco, cancelamento ou conclusão tardia quando relevantes. Evite testes que apenas reproduzem os passos internos da implementação.
- Use `ActionButtonContent` e `VectorIcon` nas ações. Não acesse partes de uma linha por índices de `Children`; preserve o quadro original dos SVGs e registre origem, licença e hash de novos vetores.

Execute `./scripts/format.ps1` antes de enviar. O CI verifica a formatação de C# e XML, compila com nullable e warnings como erros, testa comportamento, gera capturas e valida o pacote/instalação Velopack. Alterações de layout devem incluir inspeção das capturas claro/escuro e compactas disponíveis nos artefatos.

Na PR, descreva o comportamento antes/depois, a validação executada e as limitações ainda não testadas. Mudanças extensas de formatação devem ficar em um commit separado das mudanças de comportamento.
