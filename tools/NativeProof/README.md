# Prova nativa — Stage A

Projeto isolado WPF/.NET 10. Não altera o overlay do produto. A janela quadriculada e o painel pertencem ao mesmo processo; não representam um jogo. Nenhuma ação é enviada a outro aplicativo.

```powershell
dotnet build tools/NativeProof/Bezoro.NativeProof.csproj -c Release
& tools/NativeProof/bin/Release/net10.0-windows/Bezoro.NativeProof.exe --self-test --output .codex/evidence/product-native-v1/proof-v1/run-01
& tools/NativeProof/bin/Release/net10.0-windows/Bezoro.NativeProof.exe --guide-capability --output .codex/evidence/product-native-v1/proof-v1/guide-01
```

Após revisão, a execução manual aceita `--interactive --hotkey Ctrl+Alt+Space --chord LB+RB+Start --hold-ms 650 --output <diretório>`. Atalho e combinação só abrem o painel sobre a janela própria. A/B selecionam/retornam; direcional e analógico esquerdo navegam; LB/RB trocam a aba. Teclado: setas, Tab, Enter, PageUp/PageDown e Esc. Esc na superfície de teste sai. O atalho é temporário e a combinação exige soltura inicial e retenção, por controle. Guia não é inscrito.

O autoteste dura aproximadamente 30–50 s. Guarda de captura verifica HWND/PID/início, cobertura, ordem e primeiro plano antes e depois da leitura; se outra janela interferir, aborta sem salvar a captura. Essa verificação não elimina uma corrida entre verificações. Capturas e métricas são fases separadas. CPU é normalizada pelo número de processadores lógicos; latência parte de uma alteração de software, não do botão físico. RenderTier/DWM não provam aceleração; contadores de GPU podem estar indisponíveis. O processo auxiliar de contadores é próprio, sem elevação. Não execute testes de jogos ou Guide a partir desta prova.
