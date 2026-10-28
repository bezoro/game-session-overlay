using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GuideProbe;

internal static class SelfTests
{
    private static int failed, total;
    private static void Check(bool value, string reason = "assertion")
    {
        if (!value)
            throw new Exception(reason);
    }

    private static void Test(string name, Action action)
    {
        total++;
        try
        {
            action();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            failed++;
            Console.Error.WriteLine("FAIL " + name + ": " + e.Message);
        }
    }

    private static RawGuide Raw(ulong t, uint current, uint previous, double arrival = 0) => new(0, t, current, previous, arrival);
    internal static int Run()
    {
        Test("Guide DOWN UP native and arrival durations distinct", () =>
        {
            var p = new Pairing();
            Check(p.Observe(1, Raw(1000, 1, 0, 20)).Edge == "down");
            var up = p.Observe(1, Raw(1001000, 0, 1, 1090));
            Check(up.DurationNativeMs == 1000 && up.DurationArrivalMs == 1070 && up.NativeRelUs == 1000000);
        });
        Test("Guide repeated state is not another DOWN", () =>
        {
            var p = new Pairing();
            p.Observe(1, Raw(1, 1, 0));
            Check(p.Observe(1, Raw(2, 1, 1)).Edge == "repeat");
            Check(p.Observe(1, Raw(1001, 0, 1)).DurationNativeMs == 1);
        });
        Test("Guide orphan UP and duplicate DOWN incomplete", () =>
        {
            var p = new Pairing();
            Check(p.Observe(1, Raw(100, 0, 1)).DurationNativeMs is null);
            p.Observe(1, Raw(200, 1, 0));
            Check(p.Observe(1, Raw(300, 1, 0)).Status == "duplicate_down");
            Check(p.Observe(1, Raw(400, 0, 1)).DurationNativeMs is null);
        });
        Test("Guide regressed clock never fabricates duration", () =>
        {
            var p = new Pairing();
            p.Observe(1, Raw(1000, 1, 0));
            Check(p.Observe(1, Raw(900, 0, 1)).DurationNativeMs is null);
        });
        Test("Guide interleaved devices independent", () =>
        {
            var p = new Pairing();
            p.Observe(1, Raw(1000, 1, 0));
            p.Observe(2, Raw(1200, 1, 0));
            Check(p.Observe(1, Raw(2000, 0, 1)).DurationNativeMs == 1);
            Check(p.Observe(2, Raw(3200, 0, 1)).DurationNativeMs == 2);
        });
        Test("Guide still pressed at shutdown incomplete", () =>
        {
            var p = new Pairing();
            p.Observe(1, Raw(1, 1, 0));
            Check(p.Finish(120000).Single().DurationNativeMs is null);
        });
        Test("Queue capacity and overflow invalidate pairing", () =>
        {
            var q = new LimitedQueue<int>(1);
            Check(q.Push(1) && !q.Push(2) && q.Dropped == 1);
            Check(q.Pop(out var value) && value == 1 && !q.Pop(out _));
            var p = new Pairing();
            p.Observe(1, Raw(1, 1, 0));
            p.Overflow = true;
            Check(p.Observe(1, Raw(1001, 0, 1)).DurationNativeMs is null);
        });
        Test("XInput first connected held read baseline only; Guide excluded", () =>
        {
            var p = new Pads();
            Check(p.Observe(0, true, 0x1400, 0).All(r => r.Button is null));
            Check(p.Observe(0, true, 0x1000, 20).Count == 0);
            Check(p.Observe(0, true, 0, 40).Single().Edge == "up");
        });
        Test("XInput disconnect reconnect never invents DOWN", () =>
        {
            var p = new Pads();
            p.Observe(0, true, 0, 0);
            Check(p.Observe(0, false, 0, 20).Single().Edge == "disconnected");
            Check(p.Observe(0, true, 4096, 40).Single().Button is null);
            Check(p.Observe(0, true, 0, 60).Single().Edge == "up");
        });
        Test("Deadline fixed independent of events", () =>
        {
            var b = new Budget(120);
            Check(!b.Expired(119999) && b.Expired(120000) && b.Expired(120001));
        });
        Test("JSONL record cap preserves mandatory summary", () =>
        {
            using var bytes = new MemoryStream();
            var w = new Writer(bytes);
            for (var i = 0; i < 6005; i++)
                w.Write(new("test", "edge", i));
            w.Write(new("probe", "end", 1), true);
            Check(w.Written == 6001 && w.Suppressed == 5);
        });
        Test("Public ABI callback retains queue device; canonical identity balanced; stop order", Abi.Success);
        Test("Registration failure with token drains before release", Abi.RegisterFailure);
        Test("Unregister failure retains callback context instance; no unload", Abi.Retention);
        Test("v2 device enumeration HRESULT out-info and gamepad reading refs", Abi.Capabilities);
        Test("v2 reconnect and missing Guide do not fabricate input", Abi.Reconnect);
        Test("v2 controller button indices and unavailable reading", Abi.Controller);
        Test("v2 invalid info out-pointer and failure reading release", Abi.ReadFailures);
        Test("v2 partial callback registration stops and drains both tokens", Abi.Partial);
        Test("v2 unregister failure retains all context after stopping both", Abi.DeviceRetention);
        Test("v2 failed or zero-token enumeration never empty success", Abi.EnumerationFailure);
        Test("v2 callback overflow releases its temporary device ref", Abi.DeviceOverflow);
        Test("active device drain yields with replenished backlog and balanced refs", Abi.ReplenishedDrain);
        Test("classification complete empty only after successful enumeration", () =>
        {
            Check(CapabilityOutcome.Decide(true, true, true, false, 0, false, false, false).StartsWith("no_relevant_device_observed"));
            Check(CapabilityOutcome.Decide(false, false, true, false, 0, false, false, false).StartsWith("inconclusive"));
        });
        Test("classification missing/read failed/ambiguous never unsupported", () =>
        {
            Check(CapabilityOutcome.Decide(true, true, true, false, 1, true, false, false).StartsWith("inconclusive"));
            Check(CapabilityOutcome.Decide(true, true, true, true, 1, true, true, false).StartsWith("inconclusive"));
            Check(CapabilityOutcome.Decide(true, true, true, false, 2, false, true, false).StartsWith("inconclusive"));
        });
        Test("classification cancelled drop disconnect override negative", () =>
        {
            Check(CapabilityOutcome.Decide(true, true, false, false, 0, false, false, false).StartsWith("inconclusive"));
            foreach (var relevant in new[]
            {
                0,
                1
            }

            )
                Check(CapabilityOutcome.Decide(true, true, true, true, relevant, true, true, false).StartsWith("inconclusive"));
        });
        Test("classification requires explicit association and no Guide advertisement", () =>
        {
            Check(CapabilityOutcome.Decide(true, true, true, false, 1, true, true, false).StartsWith("guide_unsupported"));
            Check(CapabilityOutcome.Decide(true, true, true, false, 1, false, true, false).Contains("human_association_required"));
            Check(CapabilityOutcome.Decide(true, true, true, false, 1, true, true, true).Contains("delivery_not_proven"));
        });
        Test("shutdown late queue overflow invalidates queued Guide UP and balances refs", () => Abi.LateShutdown(false));
        Test("shutdown late log suppression invalidates final Guide drain", () => Abi.LateShutdown(true));
        Test("raw v2 descriptor stride and Guide indices0 64 127", Abi.RawLayout);
        Test("raw missing duplicate null excessive descriptors end before readings", Abi.RawDescriptors);
        Test("raw Gamepad does not suppress forced ControllerButton and hold release", Abi.RawEdges);
        Test("raw initially held baseline does not fabricate DOWN or clean pair", Abi.RawInitialHeld);
        Test("raw reading count mismatch and partial copy are inconclusive", Abi.RawReadFailure);
        Test("raw reconnect remaps descriptor and never reuses old index", Abi.RawReconnect);
        Console.WriteLine($"RESULT {total - failed}/{total}; failed={failed}; synthetic only, runtime/input not loaded");
        return failed == 0 ? 0 : 1;
    }

    private unsafe static class Abi
    {
        private static nint instance, device, table, deviceTable, info, reading, readingTable, buttonInfo;
        private static nint context;
        private static delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void> callback;
        private static readonly List<string> calls = [];
        private static int refs, readingRefs;
        private static bool remove;
        private static int registerHr, deviceHr, infoHr, readHr;
        private static uint policy, kind, buttons, lastKind, buttonCount;
        private static int partialCopy, readCalls;
        private static readonly bool[] rawButtons = new bool[128];
        private static ulong failedToken;
        private static bool zeroDeviceToken;
        private static ulong timestamp;
        private static bool nullInfo, nullReading;
        private static delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void> deviceCallback;
        private static readonly List<ulong> stops = [], unregisters = [];
        private static void Setup()
        {
            calls.Clear();
            stops.Clear();
            unregisters.Clear();
            refs = 1;
            readingRefs = 1;
            remove = true;
            registerHr = 0;
            deviceHr = 0;
            infoHr = 0;
            readHr = 0;
            policy = 0;
            lastKind = 0;
            buttonCount = 2;
            partialCopy = -1;
            readCalls = 0;
            Array.Clear(rawButtons);
            failedToken = 0;
            zeroDeviceToken = false;
            kind = 0x40000;
            buttons = 0;
            timestamp = 1000;
            nullInfo = false;
            nullReading = false;
            callback = null;
            deviceCallback = null;
            table = (nint)NativeMemory.AllocZeroed(17, (nuint)sizeof(nint));
            instance = (nint)NativeMemory.AllocZeroed(1, (nuint)sizeof(nint));
            *(nint*)instance = table;
            deviceTable = (nint)NativeMemory.AllocZeroed(4, (nuint)sizeof(nint));
            device = (nint)NativeMemory.AllocZeroed(1, (nuint)sizeof(nint));
            *(nint*)device = deviceTable;
            ((nint*)table)[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            ((nint*)table)[9] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void>, ulong*, int>)&Register;
            ((nint*)table)[11] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, void>)&Stop;
            ((nint*)table)[12] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, byte>)&Unregister;
            ((nint*)table)[16] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void>)&Focus;
            ((nint*)deviceTable)[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
            ((nint*)deviceTable)[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
            ((nint*)deviceTable)[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            info = (nint)NativeMemory.Alloc(240);
            new Span<byte>((void*)info, 240).Fill(0x5A);
            buttonInfo = (nint)NativeMemory.AllocZeroed(128, 8);
            *(uint*)(info + 224) = buttonCount;
            *(nint*)(info + 232) = buttonInfo;
            *(int*)(info + 92) = 2;
            *(uint*)(info + 96) = 0x40000;
            *(uint*)(info + 104) = 0;
            readingTable = (nint)NativeMemory.AllocZeroed(19, (nuint)sizeof(nint));
            reading = (nint)NativeMemory.AllocZeroed(1, (nuint)sizeof(nint));
            *(nint*)reading = readingTable;
            ((nint*)deviceTable)[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&DeviceInfo;
            ((nint*)table)[8] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, nint, delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void>, ulong*, int>)&RegisterDevice;
            ((nint*)table)[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)&CurrentReading;
            ((nint*)readingTable)[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            ((nint*)readingTable)[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReadingKind;
            ((nint*)readingTable)[4] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&ReadingTimestamp;
            ((nint*)readingTable)[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ButtonCount;
            ((nint*)readingTable)[9] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, byte*, uint>)&ButtonArray;
            ((nint*)readingTable)[18] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, byte>)&Gamepad;
        }

        private static void Free()
        {
            NativeMemory.Free((void*)instance);
            NativeMemory.Free((void*)table);
            NativeMemory.Free((void*)device);
            NativeMemory.Free((void*)deviceTable);
            NativeMemory.Free((void*)info);
            NativeMemory.Free((void*)reading);
            NativeMemory.Free((void*)readingTable);
            NativeMemory.Free((void*)buttonInfo);
        }

        internal static void Success()
        {
            Setup();
            try
            {
                var q = new LimitedQueue<RawGuide>(4);
                var b = new GameInput(instance, q, () => 7);
                Check(b.Start() && policy == 0x80);
                callback(44, context, device, 1000, 1, 0);
                callback(44, context, device, 2000, 0, 1);
                Check(refs == 3);
                var d = new Devices();
                Check(q.Pop(out var first));
                Check(d.Get(first.Device) == 1);
                GameInput.ReleaseObject(first.Device);
                Check(q.Pop(out var second));
                Check(d.Get(second.Device) == 1);
                GameInput.ReleaseObject(second.Device);
                Check(b.StopAndUnregister());
                Check(!q.Pop(out _));
                d.Release();
                b.Release();
                Check(refs == 1 && string.Join(',', calls) == "focus,register,stop,unregister,release");
            }
            finally
            {
                Free();
            }
        }

        internal static void RegisterFailure()
        {
            Setup();
            try
            {
                registerHr = unchecked((int)0x80004005);
                var b = new GameInput(instance, new(4), () => 0);
                Check(!b.Start());
                b.Release();
                Check(string.Join(',', calls) == "focus,register,stop,unregister,release");
            }
            finally
            {
                Free();
            }
        }

        internal static void Retention()
        {
            Setup();
            remove = false;
            var b = new GameInput(instance, new(4), () => 0);
            Check(b.Start() && !b.StopAndUnregister());
            b.Release();
            Check(!b.StopAndUnregister());
            Check(string.Join(',', calls) == "focus,register,stop,unregister");
            callback(44, context, device, 10, 1, 0);
            Check(refs == 1); /* Synthetic allocation/context intentionally retained until test-process exit, exactly like failure policy. */
        }

        private static void Drain(LimitedQueue<RawGuide> q, CapabilitySamples? sample = null)
        {
            while (q.Pop(out var raw))
            {
                try
                {
                    if (sample is not null && raw.DeviceEvent)
                        sample.Device(1, raw);
                }
                finally
                {
                    GameInput.ReleaseObject(raw.Device);
                }
            }
        }

        internal static void Capabilities()
        {
            Setup();
            try
            {
                var q = new LimitedQueue<RawGuide>(8);
                var b = new GameInput(instance, q, () => 5);
                Check(b.StartDeviceCheck() && b.EnumerationComplete && policy == 0xC0);
                var cap = GameInput.Info(device);
                Check(cap.Valid && cap.Family == 2 && cap.SupportedInput == 0x40000 && cap.SystemButtons == 0);
                var samples = new CapabilitySamples();
                Drain(q, samples);
                Check(samples.RelevantCount == 1 && !samples.AnyGuide);
                Check(samples.Poll(b, 10).Single().Edge == "baseline");
                buttons = 256;
                timestamp = 2000;
                var edge = samples.Poll(b, 20).Single();
                Check(edge.Button == "DpadLeft" && edge.Edge == "down" && readingRefs == 1);
                Check(b.StopAndUnregister());
                Check(stops.SequenceEqual(new ulong[] { 44, 55 }) && unregisters.SequenceEqual(new ulong[] { 44, 55 }));
                samples.Release();
                b.Release();
                Check(refs == 1);
            }
            finally
            {
                Free();
            }
        }

        internal static void Reconnect()
        {
            Setup();
            try
            {
                var q = new LimitedQueue<RawGuide>(8);
                var b = new GameInput(instance, q, () => 5);
                Check(b.StartDeviceCheck());
                var samples = new CapabilitySamples();
                Drain(q, samples);
                samples.Poll(b, 1).ToArray();
                deviceCallback(55, context, device, 2000, 0, 1);
                Drain(q, samples);
                Check(samples.Invalid && !samples.Poll(b, 2).Any());
                buttons = 256;
                deviceCallback(55, context, device, 3000, 1, 0);
                Drain(q, samples);
                Check(samples.Poll(b, 3).Single().Edge == "baseline");
                Check(b.StopAndUnregister());
                samples.Release();
                b.Release();
                Check(refs == 1);
            }
            finally
            {
                Free();
            }
        }

        internal static void Controller()
        {
            Setup();
            try
            {
                *(uint*)(info + 96) = 4;
                kind = 4;
                var q = new LimitedQueue<RawGuide>(8);
                var b = new GameInput(instance, q, () => 0);
                Check(b.StartDeviceCheck());
                var samples = new CapabilitySamples();
                Drain(q, samples);
                Check(samples.Poll(b, 1).Single().Edge == "baseline");
                buttons = 2;
                timestamp = 2000;
                Check(samples.Poll(b, 2).Single().Button == "Button1");
                readHr = unchecked((int)0x838A0003);
                Check(samples.Poll(b, 3).Single().Edge == "reading_unavailable" && samples.Invalid && readingRefs == 1);
                Check(b.StopAndUnregister());
                samples.Release();
                b.Release();
                Check(refs == 1);
            }
            finally
            {
                Free();
            }
        }

        internal static void ReadFailures()
        {
            Setup();
            try
            {
                var q = new LimitedQueue<RawGuide>(8);
                var b = new GameInput(instance, q, () => 0);
                Check(b.StartDeviceCheck());
                Drain(q);
                nullInfo = true;
                Check(!GameInput.Info(device).Valid);
                nullInfo = false;
                infoHr = unchecked((int)0x80004005);
                Check(!GameInput.Info(device).Valid);
                readHr = unchecked((int)0x838A0003);
                Check(!b.Read(device, 0x40000).Valid && readingRefs == 1);
                readHr = 0;
                nullReading = true;
                Check(!b.Read(device, 0x40000).Valid && readingRefs == 1);
                Check(b.StopAndUnregister());
                b.Release();
                Check(refs == 1);
            }
            finally
            {
                Free();
            }
        }

        internal static void Partial()
        {
            Setup();
            try
            {
                var q = new LimitedQueue<RawGuide>(8);
                var b = new GameInput(instance, q, () => 0);
                registerHr = unchecked((int)0x80004005);
                Check(!b.StartDeviceCheck() && b.EnumerationComplete);
                Check(stops.SequenceEqual(new ulong[] { 44, 55 }) && unregisters.SequenceEqual(new ulong[] { 44, 55 }));
                Drain(q);
                b.Release();
                Check(refs == 1 && calls.Last() == "release");
            }
            finally
            {
                Free();
            }
        }

        internal static void DeviceRetention()
        {
            Setup();
            var q = new LimitedQueue<RawGuide>(8);
            var b = new GameInput(instance, q, () => 0);
            Check(b.StartDeviceCheck());
            failedToken = 55;
            Check(!b.StopAndUnregister());
            Drain(q);
            b.Release();
            Check(!b.StopAndUnregister() && stops.SequenceEqual(new ulong[] { 44, 55 }) && unregisters.SequenceEqual(new ulong[] { 44, 55 }) && !calls.Contains("release"));
            deviceCallback(55, context, device, 4000, 1, 0);
            Check(!q.Pop(out _) && refs == 1); /* Context/objects intentionally retained to test-process exit. */
        }

        internal static void EnumerationFailure()
        {
            foreach (var zero in new[]
            {
                false,
                true
            }

            )
            {
                Setup();
                try
                {
                    deviceHr = zero ? 0 : unchecked((int)0x80004005);
                    zeroDeviceToken = zero;
                    var q = new LimitedQueue<RawGuide>(8);
                    var b = new GameInput(instance, q, () => 0);
                    Check(!b.StartDeviceCheck() && !b.EnumerationComplete);
                    Drain(q);
                    b.Release();
                    Check(refs == 1 && !calls.Contains("register"));
                }
                finally
                {
                    Free();
                }
            }
        }

        internal static void DeviceOverflow()
        {
            Setup();
            try
            {
                var q = new LimitedQueue<RawGuide>(1);
                var b = new GameInput(instance, q, () => 0);
                Check(b.StartDeviceCheck());
                deviceCallback(55, context, device, 2000, 0, 1);
                Check(q.Dropped == 1 && refs == 2);
                Check(b.StopAndUnregister());
                Drain(q);
                b.Release();
                Check(refs == 1);
            }
            finally
            {
                Free();
            }
        }

        private sealed class RawRig : IDisposable
        {
            internal readonly LimitedQueue<RawGuide> Queue = new(16);
            internal readonly GameInput Backend;
            internal readonly RawLabels Raw = new();
            internal readonly CapabilitySamples Samples = new();
            private readonly Devices registry = new();
            private readonly Pairing pairing = new();
            private readonly MemoryStream stream = new();
            private readonly Writer writer;
            internal RawRig(int index = 64)
            {
                Setup();
                kind = 0x40004;
                *(uint*)(info + 96) = kind;
                buttonCount = 128;
                *(uint*)(info + 224) = 128;
                for (var i = 0; i < 128; i++)
                {
                    *(uint*)(buttonInfo + i * 8) = (uint)(i + 2);
                    *(int*)(buttonInfo + i * 8 + 4) = 1000 + i;
                }

                if (index >= 0)
                {
                    *(int*)(buttonInfo + index * 8 + 4) = 1;
                    *(uint*)(buttonInfo + index * 8) = 0;
                }

                writer = new(stream);
                Backend = new(instance, Queue, () => 20);
                Check(Backend.StartDeviceCheck());
                Consume();
            }

            internal void Consume() => DeviceShutdown.Consume(Queue, registry, pairing, Samples, writer, Raw);
            internal List<Entry> Refresh() => Raw.Device(1, new(device, timestamp, 1, 0, 20, true));
            public void Dispose()
            {
                try
                {
                    Check(Backend.StopAndUnregister());
                    DeviceShutdown.Complete(Queue, registry, pairing, Samples, writer, 60000, Raw);
                    Raw.Release();
                    Samples.Release();
                    registry.Release();
                    Backend.Release();
                    Check(refs == 1 && readingRefs == 1, "raw cleanup refs");
                }
                finally
                {
                    stream.Dispose();
                    Free();
                }
            }
        }

        internal static void RawLayout()
        {
            foreach (var index in new[]
            {
                0,
                64,
                127
            }

            )
            {
                using var rig = new RawRig(index);
                var descriptor = GameInput.Descriptor(device);
                Check(descriptor.Valid && descriptor.Labels.Length == 128);
                Check(descriptor.Labels[index] == new RawLabel(index, 1, 0));
                var other = index == 0 ? 1 : 0;
                Check(descriptor.Labels[other] == new RawLabel(other, 1000 + other, (uint)(other + 2)), "v2 stride");
                Check(rig.Raw.Selection().Index == index && readCalls == 0, "descriptor first");
            }
        }

        internal static void RawDescriptors()
        {
            foreach (var problem in new[]
            {
                "absent",
                "duplicate",
                "null",
                "excess",
                "empty",
                "failed"
            }

            )
            {
                using var rig = new RawRig(problem == "absent" ? -1 : 64);
                if (problem == "duplicate")
                    *(int*)(buttonInfo + 65 * 8 + 4) = 1;
                if (problem == "null")
                    *(nint*)(info + 232) = 0;
                if (problem == "excess")
                    *(uint*)(info + 224) = 129;
                if (problem == "empty")
                    *(uint*)(info + 224) = 0;
                if (problem == "failed")
                    infoHr = unchecked((int)0x80004005);
                rig.Refresh();
                var result = rig.Raw.Outcome(false, false);
                Check(problem is "absent" or "empty" ? result.StartsWith("raw_label_unavailable") : result.StartsWith("inconclusive"), problem);
                Check(!rig.Raw.Poll(rig.Backend, 1).Any() && readCalls == 0, "negative descriptor must not read");
            }
        }

        internal static void RawEdges()
        {
            using var rig = new RawRig();
            Check(rig.Samples.Poll(rig.Backend, 1).Single().Edge == "baseline" && lastKind == 0x40000);
            Check(rig.Raw.Poll(rig.Backend, 2).Any(e => e.Edge == "ready") && lastKind == 4, "Gamepad must not suppress raw");
            rawButtons[64] = true;
            timestamp = 2000;
            Check(rig.Raw.Poll(rig.Backend, 10).Single().Edge == "down");
            timestamp = 3000;
            Check(!rig.Raw.Poll(rig.Backend, 20).Any(), "hold not repeated");
            rawButtons[64] = false;
            timestamp = 1002000;
            var up = rig.Raw.Poll(rig.Backend, 1020).Single();
            Check(up.Edge == "up" && up.Status == "paired" && up.DurationNativeMs == 1000 && up.DurationArrivalMs == 1010);
            Check(rig.Raw.Outcome(true, false, true, true).StartsWith("raw_guide_observed"));
            Check(rig.Raw.Outcome(true, false).StartsWith("inconclusive"), "human confirmation required");
        }

        internal static void RawInitialHeld()
        {
            using var rig = new RawRig();
            rawButtons[64] = true;
            var initial = rig.Raw.Poll(rig.Backend, 1).ToArray();
            Check(initial.All(e => e.Edge != "down"));
            timestamp = 2000;
            Check(!rig.Raw.Poll(rig.Backend, 2).Any());
            rawButtons[64] = false;
            timestamp = 3000;
            var up = rig.Raw.Poll(rig.Backend, 3).Single();
            Check(up.Edge == "up" && up.DurationNativeMs is null && rig.Raw.PairedCycles == 0);
            Check(rig.Raw.Outcome(true, false, true, true).StartsWith("inconclusive"));
        }

        internal static void RawReadFailure()
        {
            foreach (var partial in new[]
            {
                false,
                true
            }

            )
            {
                using var rig = new RawRig();
                if (partial)
                    partialCopy = 127;
                else
                    buttonCount = 127;
                Check(rig.Raw.Poll(rig.Backend, 1).Single().Edge == "reading_invalid");
                Check(rig.Raw.Invalid && rig.Raw.Outcome(true, false, true, true).StartsWith("inconclusive") && readingRefs == 1);
            }
        }

        internal static void RawReconnect()
        {
            using var rig = new RawRig(0);
            rig.Raw.Poll(rig.Backend, 1).ToArray();
            deviceCallback(55, context, device, 2000, 0, 1);
            rig.Consume();
            *(int*)(buttonInfo + 4) = 1000;
            *(int*)(buttonInfo + 127 * 8 + 4) = 1;
            deviceCallback(55, context, device, 3000, 1, 0);
            rig.Consume();
            var labels = rig.Refresh();
            Check(labels.Single(e => e.Label == 1).LabelIndex == 127, "reconnect descriptor remapped");
            var reads = readCalls;
            Check(!rig.Raw.Poll(rig.Backend, 3).Any() && readCalls == reads, "disconnected observation cannot reuse prior index");
            Check(rig.Raw.Outcome(true, false, true, true).StartsWith("inconclusive"));
        }

        internal static void LateShutdown(bool suppression)
        {
            Setup();
            try
            {
                var q = new LimitedQueue<RawGuide>(1);
                double now = 20;
                var b = new GameInput(instance, q, () => now);
                Check(b.StartDeviceCheck());
                var registry = new Devices();
                var pairing = new Pairing();
                var samples = new CapabilitySamples();
                using var stream = new MemoryStream();
                var writer = new Writer(stream);
                DeviceShutdown.Consume(q, registry, pairing, samples, writer);
                callback(44, context, device, 1000, 1, 0);
                DeviceShutdown.Consume(q, registry, pairing, samples, writer);
                Check(q.Dropped == 0 && !writer.Limited && !pairing.Overflow && !samples.Invalid, "last loop check must be clean");
                now = 1040;
                callback(44, context, device, 1001000, 0, 1);
                if (suppression)
                {
                    while (writer.Written < 6000)
                        writer.Write(new("test", "fill", 1000));
                    writer.Write(new("test", "suppressed", 1001));
                    Check(writer.Limited && q.Dropped == 0);
                }
                else
                {
                    callback(44, context, device, 1002000, 0, 0);
                    Check(q.Dropped == 1);
                }

                Check(b.StopAndUnregister());
                DeviceShutdown.Complete(q, registry, pairing, samples, writer, 60000);
                var invalid = samples.Invalid;
                var flagged = pairing.Overflow;
                var outcome = CapabilityOutcome.Decide(true, true, true, invalid || q.Dropped > 0 || writer.Limited, samples.RelevantCount, false, samples.HasCommonReading, samples.AnyGuide);
                Entry? up = null;
                if (!suppression)
                {
                    var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var entry = System.Text.Json.JsonSerializer.Deserialize(line, LogJson.Default.Entry);
                        if (entry?.Source == "gameinput" && entry.Edge == "up")
                            up = entry;
                    }

                    Console.WriteLine("OBSERVED shutdown UP: " + System.Text.Json.JsonSerializer.Serialize(up, LogJson.Default.Entry));
                }

                samples.Release();
                registry.Release();
                b.Release();
                Check(refs == 1 && readingRefs == 1, "shutdown refs must balance");
                Check(outcome.StartsWith("inconclusive"));
                if (!suppression)
                    Check(up is { Status: "overflow_unreliable", DurationNativeMs: null, DurationArrivalMs: null }, "late queue drop emitted a falsely clean pair");
                Check(invalid && flagged, "shutdown must propagate late queue/log loss before consuming UP");
            }
            finally
            {
                Free();
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int RegisterDevice(nint self, nint filter, uint input, uint status, uint enumeration, nint ctx, delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void> cb, ulong* token)
        {
            context = ctx;
            deviceCallback = cb;
            *token = zeroDeviceToken ? 0ul : 55ul;
            calls.Add("device-register");
            if (input != 0x4000E || status != 1 || enumeration != 2)
                return unchecked((int)0x80070057);
            if (deviceHr >= 0 && !zeroDeviceToken)
                cb(55, ctx, device, 1000, 1, 0);
            return deviceHr;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int DeviceInfo(nint self, nint* value)
        {
            *value = nullInfo ? 0 : info;
            return infoHr;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CurrentReading(nint self, uint input, nint target, nint* value)
        {
            *value = nullReading ? 0 : reading;
            lastKind = input;
            readCalls++;
            if (*value != 0)
                readingRefs++;
            return target == device && (input & kind) == input ? readHr : unchecked((int)0x80070057);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReadingKind(nint self) => lastKind;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static ulong ReadingTimestamp(nint self) => timestamp;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ButtonCount(nint self) => buttonCount;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ButtonArray(nint self, uint count, byte* values)
        {
            for (var i = 0; i < count; i++)
                values[i] = (byte)(rawButtons[i] || (i < 32 && (buttons & (1u << i)) != 0) ? 1 : 0);
            return partialCopy < 0 ? count : (uint)partialCopy;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static byte Gamepad(nint self, nint value)
        {
            new Span<byte>((void*)value, 28).Clear();
            *(uint*)value = buttons;
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Focus(nint self, uint flags)
        {
            policy = flags;
            calls.Add("focus");
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Register(nint self, nint filter, uint buttons, nint ctx, delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void> cb, ulong* token)
        {
            context = ctx;
            callback = cb;
            *token = 44;
            calls.Add("register");
            return registerHr;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void Stop(nint self, ulong token)
        {
            calls.Add("stop");
            stops.Add(token);
            if (callback != null)
                callback(44, context, device, 3000, 1, 0);
            if (deviceCallback != null)
                deviceCallback(55, context, device, 3000, 1, 0);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static byte Unregister(nint self, ulong token)
        {
            calls.Add("unregister");
            unregisters.Add(token);
            return remove && token != failedToken ? (byte)1 : (byte)0;
        }

        private static int refillRemaining, queryCalls;
        internal static void ReplenishedDrain()
        {
            Setup();
            try
            {
                var q = new LimitedQueue<RawGuide>(256);
                var b = new GameInput(instance, q, () => 20);
                var registry = new Devices();
                var pairing = new Pairing();
                var samples = new CapabilitySamples();
                using var stream = new MemoryStream();
                var writer = new Writer(stream);
                Check(b.Start());
                queryCalls = 0;
                refillRemaining = 600;
                callback(44, context, device, 1000, 1, 0);
                DeviceShutdown.Consume(q, registry, pairing, samples, writer);
                var consumed = queryCalls;
                var backlog = q.Pop(out var pending);
                if (backlog)
                    GameInput.ReleaseObject(pending.Device);
                refillRemaining = 0;
                Check(b.StopAndUnregister());
                DeviceShutdown.Complete(q, registry, pairing, samples, writer, 60000);
                samples.Release();
                registry.Release();
                b.Release();
                Console.WriteLine($"OBSERVED active replenishment: consumed={consumed}; backlog={backlog}; refs={refs}; readingRefs={readingRefs}");
                Check(refs == 1 && readingRefs == 1, "replenished queue references must balance");
                Check(consumed == 256 && backlog, "active drain must yield after capacity, leaving callbacks for next deadline/cancel check");
            }
            finally
            {
                refillRemaining = 0;
                Free();
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Query(nint self, Guid* id, nint* result)
        {
            *result = device;
            refs++;
            queryCalls++;
            if (refillRemaining > 0)
            {
                refillRemaining--;
                callback(44, context, device, 2000 + (ulong)queryCalls, 1, 1);
            }

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(nint self) => (uint)++refs;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(nint self)
        {
            if (self == device)
                return (uint)--refs;
            if (self == reading)
                return (uint)--readingRefs;
            calls.Add("release");
            return 0;
        }
    }
}
