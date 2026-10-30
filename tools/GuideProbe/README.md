# GuideProbe

Ferramenta de diagnóstico .NET 10/Windows x64 para observar GameInput v2 e XInput. É separada da overlay: não abre sua interface, não envia teclas, não intercepta o controle e não altera ajustes do produto. A política atual de ativação do produto está no [README principal](../../README.md).

A solução compila esta ferramenta e o CI executa apenas testes sintéticos:

```powershell
dotnet tools/GuideProbe/bin/Release/net10.0-windows/GuideProbe.dll --self-test
```

## Consulta explícita de hardware

Execute em uma sessão Windows quando quiser investigar um problema real. O destino deve ser um arquivo novo; Ctrl+C cancela a observação.

```powershell
# Capacidades e botões anunciados; até 60 segundos.
GuideProbe.exe --device-check --output capabilities.jsonl

# Procura um label público XboxGuide único antes de observar bordas.
GuideProbe.exe --raw-guide-check --output raw-guide.jsonl

# Observação passiva com duração de 90 a 120 segundos.
GuideProbe.exe --duration-seconds 90 --output guide.jsonl
```

GameInput usa os contratos públicos v2. Identidades efêmeras agrupam eventos apenas nesta execução, sem registrar ponteiros, seriais, títulos de janelas ou mapear automaticamente um dispositivo a um slot XInput. As opções de capacidades usam a política local `BackgroundInput + BackgroundGuide`; o modo passivo usa `BackgroundGuide`. Falha em uma fonte não prova ausência global do botão Guide.

Os registros distinguem tempos nativos em microssegundos, chegada monotônica em milissegundos e durações de pares down/up. Bordas órfãs, repetição, relógio regressivo, desconexão, overflow ou leitura incompleta não produzem uma duração confirmada. Limites: fila de 256, 16 dispositivos, 6.000 registros ordinários e cópias de até 128 botões/labels.

A limpeza interrompe todos os tokens antes de removê-los, drena callbacks e só então libera dispositivos, contexto e DLL. Se o runtime não confirmar a remoção, essas referências permanecem retidas até a saída do processo para impedir uso após liberação. O resultado fica incompleto.

A observação isolada não associa um dispositivo ao controle físico usado pela pessoa. Entrega do Guide, suporte anunciado e funcionamento na TV são conclusões diferentes; os relatórios preservam `inconclusive` quando essa associação ou as leituras necessárias não foram confirmadas.

Referência do callback público: [GameInputSystemButtonCallback](https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/functions/gameinputsystembuttoncallback). Os testes sintéticos cobrem assinaturas v2, ponteiros de saída, offsets, reconexão, retenção de referências e classificação; não consultam hardware.
