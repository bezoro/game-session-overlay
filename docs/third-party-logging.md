# Logging dependencies

Command diagnostics use the following pinned packages. Their original NuGet metadata declares `Apache-2.0` for all four.

| Package | Version | Purpose | Official source |
| --- | --- | --- | --- |
| [Serilog](https://www.nuget.org/packages/Serilog/4.4.0) | 4.4.0 | Structured events and per-pipeline failure reporting | [serilog/serilog](https://github.com/serilog/serilog) |
| [Serilog.Sinks.File](https://www.nuget.org/packages/Serilog.Sinks.File/7.0.0) | 7.0.0 | Shared files, size/time rotation and retention | [serilog/serilog-sinks-file](https://github.com/serilog/serilog-sinks-file) |
| [Serilog.Sinks.Async](https://www.nuget.org/packages/Serilog.Sinks.Async/2.1.0) | 2.1.0 | Bounded background queue and loss monitoring | [serilog/serilog-sinks-async](https://github.com/serilog/serilog-sinks-async) |
| [Serilog.Formatting.Compact](https://www.nuget.org/packages/Serilog.Formatting.Compact/3.0.0) | 3.0.0 | Compact newline-delimited JSON events | [serilog/serilog-formatting-compact](https://github.com/serilog/serilog-formatting-compact) |

The published application includes [package attribution](../src/ApolloOverlay/Assets/ThirdParty/Logging/ATTRIBUTIONS.txt), the original [Serilog license](../src/ApolloOverlay/Assets/ThirdParty/Logging/Serilog-LICENSE.txt), and the identical [Apache 2.0 license text](../src/ApolloOverlay/Assets/ThirdParty/Logging/Apache-2.0.txt) supplied by the three sink/formatter repositories. Both original license variants are retained without modification. The upstream source trees and NuGet packages at these pinned versions contain no separate `NOTICE` file; this project does not fabricate one.

[Provenance](../src/ApolloOverlay/Assets/ThirdParty/Logging/provenance.json) records each NuGet version, upstream repository commit, original license URL, file hash and declared package authors. Copyright text is included only where present in the original package metadata. The project file copies these attribution and license files into published packages under `Assets/ThirdParty/Logging`.

The asynchronous journal supports debugging; it is not a security audit ledger. Abrupt termination, unavailable storage or a full queue can lose records. Health counters and loss events report observed gaps, and a completed drain confirms processing of accepted events rather than a guarantee of durable storage. Diagnostic events do not certify native effects, controller isolation or game compatibility.
