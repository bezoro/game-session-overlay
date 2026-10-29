# Native proof, Stage A

This isolated WPF/.NET 10 project does not change the product overlay. Its checkerboard window and panel belong to the same process and do not represent a game. It sends no action to another application.

```powershell
dotnet build tools/NativeProof/Bezoro.NativeProof.csproj -c Release
& tools/NativeProof/bin/Release/net10.0-windows/Bezoro.NativeProof.exe --self-test --output artifacts/native-proof/run-01
& tools/NativeProof/bin/Release/net10.0-windows/Bezoro.NativeProof.exe --guide-capability --output artifacts/native-proof/guide-01
```

After review, manual execution accepts `--interactive --hotkey Ctrl+Alt+Space --chord LB+RB+Start --hold-ms 650 --output <directory>`. The hotkey and chord open the panel only over its owned window. A/B select and go back; the D-pad and left stick navigate; LB/RB switch tabs. Keyboard input supports arrows, Tab, Enter, PageUp/PageDown and Esc. Esc exits on the test window. The hotkey is temporary. The chord requires initial release and a hold for each controller. Guide is not registered.

The self-test takes about 30–50 seconds. Capture guards check HWND/PID/start time, coverage, order and foreground before and after reading pixels. Interference by another window aborts capture without saving it. These checks do not eliminate a race between observations. Captures and measurements run as separate phases. CPU is normalized by logical processor count; latency starts at a software change, not a physical button press. RenderTier/DWM do not prove acceleration, and GPU counters may be unavailable. The counter helper belongs to this tool and runs without elevation. Do not use this proof to test games or Guide.

The project is part of the solution and builds in CI. `--input-tests --output artifacts/native-proof/input` runs navigation fixtures only, without starting the window scenario. Configurable chords belong to this historical proof tool and do not describe the product's current activation policy.
