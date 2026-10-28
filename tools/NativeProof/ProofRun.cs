using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ApolloOverlay;

namespace Bezoro.NativeProof;

internal static class ProofRun
{
    private static LatencyResult Summarize(string kind, List<double> values)
    {
        var ordered = values.Order().ToArray();
        return new(kind, values.Count, ordered[(int)Math.Ceiling(values.Count * .5) - 1], ordered[(int)Math.Ceiling(values.Count * .95) - 1], ordered[^1], values);
    }

    private static async Task<CostResult> Cost(string phase, int milliseconds)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var start = process.PrivateMemorySize64;
        var lastCpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        var lastTime = watch.Elapsed.TotalMilliseconds;
        var samples = new List<CostSample>();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            await Task.Delay(250);
            process.Refresh();
            var now = watch.Elapsed.TotalMilliseconds;
            var cpu = process.TotalProcessorTime;
            samples.Add(new(now, (cpu - lastCpu).TotalMilliseconds / (now - lastTime) / Environment.ProcessorCount * 100, process.PrivateMemorySize64));
            lastCpu = cpu;
            lastTime = now;
        }

        return new(phase, watch.Elapsed.TotalMilliseconds, samples.Average(s => s.CoreNormalizedCpuPercent), samples.Max(s => s.CoreNormalizedCpuPercent), start, samples[^1].PrivateBytes, samples);
    }

    private static async Task Gpu(string output, ProofReport report)
    {
        var prefix = Path.Combine(output, "gpu-counter");
        var command = @"$ErrorActionPreference='Stop'; Get-Counter -Counter '\GPU Engine(pid_" + Environment.ProcessId + @"_*)\Utilization Percentage' -SampleInterval 1 -MaxSamples 3 | ForEach-Object { $_.CounterSamples | Select-Object Path,CookedValue,Status } | ConvertTo-Json -Depth 3";
        var start = new ProcessStartInfo("C:/Program Files/PowerShell/7/pwsh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using var child = new Process
        {
            StartInfo = start
        };
        var time = Stopwatch.StartNew();
        child.Start();
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException)
        {
            child.Kill();
            await child.WaitForExitAsync();
            report.Gaps.Add("Own-PID GPU counter timed out; actual acceleration unproven.");
        }

        var text = await stdout;
        var error = await stderr;
        File.WriteAllText(prefix + ".stdout.json", text);
        File.WriteAllText(prefix + ".stderr.txt", error);
        File.WriteAllText(prefix + ".command.txt", command + Environment.NewLine + $"exit={child.ExitCode}; elapsedMs={time.Elapsed.TotalMilliseconds:F2}; ownChildPid={child.Id}");
        report.Facts["gpuCounterExit"] = child.ExitCode.ToString();
        report.Facts["gpuCounterOutput"] = prefix + ".stdout.json";
        if (child.ExitCode != 0 || string.IsNullOrWhiteSpace(text))
            report.Gaps.Add("Own-PID GPU Engine counters unavailable/read failed; RenderTier/DWM/default mode do not prove hardware acceleration.");
        else
            report.Gaps.Add("Own-PID GPU Engine samples retained; GPU activity is not proof every transparent-window frame used hardware acceleration.");
    }

    internal static async Task Execute(ProofConfig config, string output, ProofReport report)
    {
        using var windows = new ProofWindows(config);
        windows.Exit = () => throw new InvalidOperationException("Proof interrupted through owned UI.");
        report.Facts["processId"] = Environment.ProcessId.ToString();
        report.Facts["processStartTicks"] = windows.Identity.ToString();
        report.Facts["renderTier"] = (RenderCapability.Tier >> 16).ToString();
        report.Facts["processRenderMode"] = RenderOptions.ProcessRenderMode.ToString();
        report.Facts["dwmHresult"] = OwnedNative.DwmIsCompositionEnabled(out var dwm).ToString();
        report.Facts["dwmEnabled"] = dwm.ToString();
        report.Facts["cpuDenominatorLogicalProcessors"] = Environment.ProcessorCount.ToString();
        report.Facts["hotkeyRegistered"] = windows.HotkeyRegistered.ToString();
        report.Facts["hotkeyError"] = windows.HotkeyError.ToString();
        report.Facts["latencyMeaning"] = "Software ingress to one-shot Rendering and separately screen pixel readback; not physical button latency. Pixel polling overhead included.";
        if (!windows.HotkeyRegistered)
            report.Gaps.Add("Temporary hotkey registration refused, error=" + windows.HotkeyError + "; collision must be resolved before interactive acceptance.");
        await windows.NextFrame(() => windows.Show());
        await Task.Delay(300);
        OwnedNative.GetWindowRect(windows.OverlayHandle, out var bounds);
        var capture = new OwnedNative.Rect
        {
            Left = bounds.Left - 6,
            Top = bounds.Top - 6,
            Right = bounds.Right + 6,
            Bottom = bounds.Bottom + 6
        };
        var dpi = VisualTreeHelper.GetDpi(windows.Overlay);
        report.Facts["dpi"] = dpi.PixelsPerInchX + "x" + dpi.PixelsPerInchY;
        report.Facts["overlayBounds"] = $"{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}";
        report.Facts["captureBounds"] = $"{capture.Left},{capture.Top},{capture.Width},{capture.Height}";
        report.Facts["overlayExtendedStyle"] = "0x" + OwnedNative.GetWindowLongPtr(windows.OverlayHandle, -20).ToInt64().ToString("X");
        byte[] Capture(string name)
        {
            var path = Path.Combine(output, name + ".png");
            var pixels = OwnedNative.Capture(windows.UnderlayHandle, windows.OverlayHandle, capture, windows.Identity, path);
            report.Captures.Add(path);
            Console.WriteLine("CAPTURE " + path);
            return pixels;
        }

        windows.Hide();
        await Task.Delay(180);
        var baseA = Capture("underlay-a");
        await windows.NextFrame(() => windows.Show());
        await Task.Delay(150);
        var composedA = Capture("composed-a");
        windows.Hide();
        windows.SetPattern(true);
        await windows.NextFrame(() =>
        {
        });
        await Task.Delay(150);
        var baseB = Capture("underlay-b");
        await windows.NextFrame(() => windows.Show());
        await Task.Delay(150);
        var composedB = Capture("composed-b");
        int Difference(byte[] a, byte[] b, int x, int y)
        {
            var i = (y * capture.Width + x) * 4;
            return Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }

        var panelX = (int)(18 * dpi.DpiScaleX) + 6;
        var panelY = (int)(18 * dpi.DpiScaleY) + 6;
        report.Cases.Add(new("outside transparent gutter matches owned underlay", Difference(baseA, composedA, 7, 7) <= 6 && Difference(baseB, composedB, 7, 7) <= 6, null));
        report.Cases.Add(new("rounded corner reveals owned underlay", Difference(baseA, composedA, panelX + 1, panelY + 1) <= 20, null));
        var insideX = panelX + (int)(8 * dpi.DpiScaleX);
        var insideY = capture.Height / 2;
        report.Cases.Add(new("surface translucency changes with real underlay", Difference(composedA, composedB, insideX, insideY) > 4 && Difference(baseA, composedA, insideX, insideY) > 20, null));
        report.Facts["translucencyRgbDistance"] = Difference(composedA, composedB, insideX, insideY).ToString();
        report.Facts["shadowRgbDistance"] = Difference(baseA, composedA, panelX - 3, insideY).ToString();
        var showTimes = new List<double>();
        var renderTimes = new List<double>();
        var pixelTimes = new List<double>();
        windows.Hide();
        for (var i = 0; i < 30; i++)
        {
            if (OwnedNative.GetForegroundWindow() != windows.UnderlayHandle)
                throw new InvalidOperationException("Lifecycle aborted: another application took foreground.");
            var focused = false;
            showTimes.Add(await windows.NextFrame(() => focused = windows.Show()));
            if (i is 0 or 29)
            {
                Capture("transition-show-" + i);
                await Task.Delay(80);
                Capture("transition-settled-" + i);
            }

            var target = i % 2 == 0 ? Colors.DeepSkyBlue : Colors.LimeGreen;
            var clock = Stopwatch.StartNew();
            renderTimes.Add(await windows.NextFrame(() => windows.Marker.Background = new SolidColorBrush(target)));
            var point = windows.Marker.PointToScreen(new Point(15, 15));
            var seen = false;
            while (clock.ElapsedMilliseconds < 500)
            {
                var pixel = OwnedNative.Pixel(windows.UnderlayHandle, windows.OverlayHandle, (int)point.X, (int)point.Y, windows.Identity);
                if (Math.Abs(pixel.R - target.R) + Math.Abs(pixel.G - target.G) + Math.Abs(pixel.B - target.B) < 8)
                {
                    seen = true;
                    break;
                }

                await Task.Delay(2);
            }

            if (!seen)
                throw new InvalidOperationException("Marker did not reach owned screen pixels within 500ms.");
            pixelTimes.Add(clock.Elapsed.TotalMilliseconds);
            var shown = OwnedNative.GetForegroundWindow();
            var count = Application.Current.Windows.Cast<Window>().Count(w => w.IsVisible);
            var returned = windows.Hide();
            await Task.Delay(35);
            report.Cycles.Add(new(i + 1, windows.UnderlayHandle.ToInt64(), windows.OverlayHandle.ToInt64(), shown.ToInt64(), OwnedNative.GetForegroundWindow().ToInt64(), focused, returned, count, windows.ActiveRenderHandlers));
        }

        report.Latencies.Add(Summarize("warm show → Rendering callback (software)", showTimes));
        report.Latencies.Add(Summarize("marker ingress → Rendering callback (software)", renderTimes));
        report.Latencies.Add(Summarize("marker ingress → owned screen pixel (software + readback overhead)", pixelTimes));
        report.Cases.Add(new("30 focus and return cycles without duplicate windows or render subscribers", report.Cycles.Count == 30 && report.Cycles.All(c => c.ShowFocus && c.ReturnFocus && c.VisibleOwnedWindows == 2 && c.RenderSubscribers == 0), null));
        await Task.Delay(1000);
        var events = windows.InputEvents;
        report.Costs.Add(await Cost("hidden (100ms chord poll only)", 10000));
        windows.Show();
        await Task.Delay(1000);
        report.Costs.Add(await Cost("static open (30ms navigation poll)", 10000));
        var transform = new TranslateTransform();
        windows.Marker.RenderTransform = transform;
        var gpu = Gpu(output, report);
        var animation = new DoubleAnimation(0, -80, TimeSpan.FromSeconds(.5))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(4)
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
        report.Costs.Add(await Cost("bounded marker animation, GPU helper overlaps", 4000));
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        await gpu;
        report.Facts["inputEventsDuringMetrics"] = (windows.InputEvents - events).ToString();
        report.Facts["idleRenderingSubscribers"] = windows.ActiveRenderHandlers.ToString();
        for (uint slot = 0; slot < 4; slot++)
            if (Native.XInputGetState(slot, out _) == 0)
                report.ConnectedSlots.Add((int)slot);
        if (report.ConnectedSlots.Count > 0)
        {
            var packets = new Dictionary<int, uint>();
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 15000)
            {
                foreach (var slot in report.ConnectedSlots)
                    if (Native.XInputGetState((uint)slot, out var state) == 0)
                    {
                        if (packets.TryGetValue(slot, out var previous) && previous != state.Packet)
                            report.ObservedPacketChanges++;
                        packets[slot] = state.Packet;
                    }

                await Task.Delay(50);
            }

            report.Gaps.Add($"XInput slots connected={string.Join(',', report.ConnectedSlots)}; observed packet changes={report.ObservedPacketChanges}; bus/physical identity and complete physical navigation remain unverified.");
        }
        else
            report.Gaps.Add("No XInput controller connected; 15s physical observation skipped, physical input remains unexecuted.");
        report.Gaps.Add("Guide edges/long hold and preservation of Steam short press unexecuted; no callback or exclusive policy used.");
        report.Gaps.Add("Owned borderless fixture does not certify games, exclusive fullscreen, Bluetooth/virtual controllers, mixed-DPI, Apollo or Aurora.");
        report.Cases.Add(new("no persistent Rendering subscription after metrics", windows.ActiveRenderHandlers == 0, null));
        Console.WriteLine("COST " + string.Join("; ", report.Costs.Select(c => $"{c.Phase}: CPU mean={c.CpuMeanPercent:F3}% peak={c.CpuPeakPercent:F3}% private={c.PrivateStart}->{c.PrivateEnd}")));
    }
}
