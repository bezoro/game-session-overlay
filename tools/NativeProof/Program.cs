using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using ApolloOverlay;

namespace Bezoro.NativeProof;

internal sealed record CaseResult(string Name, bool Passed, string? Error);
internal sealed record GuideResult(bool InterfaceAvailable, string Reason, bool CallbacksRegistered, bool EdgesObserved, string LongPress, string SteamShortPress);
internal sealed record CycleResult(int Cycle, long Underlay, long Overlay, long ForegroundShown, long ForegroundReturned, bool ShowFocus, bool ReturnFocus, int VisibleOwnedWindows, int RenderSubscribers);
internal sealed record CostSample(double ElapsedMs, double CoreNormalizedCpuPercent, long PrivateBytes);
internal sealed record CostResult(string Phase, double DurationMs, double CpuMeanPercent, double CpuPeakPercent, long PrivateStart, long PrivateEnd, List<CostSample> Samples);
internal sealed record LatencyResult(string Kind, int Count, double P50Ms, double P95Ms, double MaxMs, List<double> Samples);
internal sealed class ProofReport
{
    public string Status { get; set; } = "running";
    public string? Failure
    {
        get; set;
    }
    public List<CaseResult> Cases { get; set; } = [];
    public List<CycleResult> Cycles { get; set; } = [];
    public List<CostResult> Costs { get; set; } = [];
    public List<LatencyResult> Latencies { get; set; } = [];
    public List<string> Captures { get; set; } = [];
    public Dictionary<string, string> Facts { get; set; } = [];
    public List<int> ConnectedSlots { get; set; } = [];
    public int ObservedPacketChanges
    {
        get; set;
    }
    public List<string> Gaps { get; set; } = [];
}

[JsonSerializable(typeof(ProofReport))]
[JsonSerializable(typeof(GuideResult))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ProofJson : JsonSerializerContext;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string Value(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i < 0 ? fallback : i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing argument: " + name);
        }

        var output = Path.GetFullPath(Value("--output", "proof-output"));
        Directory.CreateDirectory(output);
        if (args.Contains("--input-tests"))
        {
            var input = new ProofReport
            {
                Cases = ProofNavigation.Tests()
            };
            input.Status = input.Cases.All(c => c.Passed) ? "passed" : "failed";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(input, ProofJson.Default.ProofReport));
            foreach (var c in input.Cases)
                Console.WriteLine($"{(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Error}");
            return input.Status == "passed" ? 0 : 1;
        }

        if (args.Contains("--guide-capability"))
        {
            var backend = new GameInputBackend();
            bool available;
            string reason;
            try
            {
                available = backend.Initialize(out reason);
            }
            finally
            {
                backend.Release();
            }

            var result = new GuideResult(available, reason, false, false, "unexecuted: no both-edge observer", "unknown: Steam was not operated");
            File.WriteAllText(Path.Combine(output, "guide.json"), JsonSerializer.Serialize(result, ProofJson.Default.GuideResult));
            Console.WriteLine($"GameInput public v2 available={available}; no callback or focus policy registered. {reason}");
            return 0;
        }

        var config = ProofConfig.Parse(Value("--hotkey", "Ctrl+Alt+Space"), Value("--chord", "LB+RB+Start"), int.Parse(Value("--hold-ms", "650"), System.Globalization.CultureInfo.InvariantCulture));
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        var exit = 0;
        app.Startup += async (_, _) =>
        {
            if (args.Contains("--focused-test"))
            {
                try
                {
                    var focused = await FocusedInput.Execute(output);
                    exit = focused.Status == "passed" ? 0 : 1;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    exit = 1;
                }

                app.Shutdown(exit);
                return;
            }

            if (args.Contains("--interactive"))
            {
                var windows = new ProofWindows(config);
                windows.Exit = () =>
                {
                    windows.Dispose();
                    app.Shutdown();
                };
                windows.Show();
                return;
            }

            if (!args.Contains("--self-test"))
            {
                Console.Error.WriteLine("Use --self-test, --guide-capability, or --interactive.");
                app.Shutdown(2);
                exit = 2;
                return;
            }

            var report = new ProofReport
            {
                Cases = ProofNavigation.Tests()
            };
            try
            {
                await ProofRun.Execute(config, output, report);
                report.Status = report.Cases.All(c => c.Passed) ? "passed-with-explicit-gaps" : "failed";
                if (report.Status == "failed")
                    exit = 1;
            }
            catch (Exception e)
            {
                report.Status = "failed";
                report.Failure = e.ToString();
                Console.Error.WriteLine(e);
                exit = 1;
            }
            finally
            {
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, ProofJson.Default.ProofReport));
                Console.WriteLine($"Proof {report.Status}: {report.Cases.Count(c => c.Passed)}/{report.Cases.Count} deterministic/runtime cases, {report.Cycles.Count} cycles, {report.Captures.Count} captures.");
                app.Shutdown(exit);
            }
        };
        app.Run();
        return exit;
    }
}
