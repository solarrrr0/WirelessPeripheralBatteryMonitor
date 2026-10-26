using System.Text.Json;
using PeripheralBatteryTray.Windows;

internal static class AtkCases
{
    private static AtkHidDescriptor Descriptor => new(0x373B, 0x11FD, 1, 0xFF02, 2, 17, 17, 0, true, true);
    internal static void Run(Action<string, string, Action> run, Action<bool> check)
    {
        void Wait(Func<bool> predicate) => check(SpinWait.SpinUntil(predicate, 1000));
        run("AT01", "ATK known request vector and malformed replies cannot become trusted readings", () =>
        {
            check(Convert.ToHexString(AtkProtocol.BuildRequest(AtkReadCommand.Battery)) == "0804000000000000000000000000000049");
            bool rejected = false; try { AtkProtocol.BuildRequest((AtkReadCommand)5); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected);
            var report = Reply(AtkReadCommand.Battery, 73);
            check(AtkProtocol.InspectReply(report, AtkReadCommand.Battery, out var observation) == AtkReplyError.None
                && observation is { PercentCandidate: 73, MatchesRequestChecksumConvention: true });
            report[0] = 7; check(AtkProtocol.InspectReply(report, AtkReadCommand.Battery, out _) == AtkReplyError.ReportId);
            report[0] = 8; check(AtkProtocol.InspectReply(report.AsSpan(0, 10), AtkReadCommand.Battery, out _) == AtkReplyError.Length);
            check(AtkProtocol.InspectReply(report, AtkReadCommand.MouseIdentity, out _) == AtkReplyError.Command);
            check(AtkProtocol.InspectReply(AtkProtocol.BuildRequest(AtkReadCommand.Battery), AtkReadCommand.Battery, out _) == AtkReplyError.Echo);
            foreach (int value in new[] { 0, 100, 101 })
            {
                check(AtkProtocol.InspectReply(Reply(AtkReadCommand.Battery, value), AtkReadCommand.Battery, out observation) == AtkReplyError.None
                    && (observation!.PercentCandidate is not null) == (value <= 100));
            }
            report = Reply(AtkReadCommand.Battery, 63); report[16] ^= 1;
            check(AtkProtocol.InspectReply(report, AtkReadCommand.Battery, out observation) == AtkReplyError.None
                && observation!.MatchesRequestChecksumConvention == false); // Observation only, no invented response CRC rule.
            // Reduced form of the observed two-byte layout; no private frame is embedded.
            report = Reply(AtkReadCommand.Battery, 95); report[5] = 2;
            report[8] = 16; report[9] = 19; // Bytes outside declared data must not become voltage.
            report[16] = 0; report[16] = unchecked((byte)(0x55 - report.Sum(b => b)));
            check(AtkProtocol.InspectReply(report, AtkReadCommand.Battery, out observation) == AtkReplyError.None
                && observation is { PercentCandidate: 95, RawCharge: 1, RawVoltageHigh: null, RawVoltageLow: null,
                    DeclaredDataLength: 2, MatchesRequestChecksumConvention: true });
            report[5] = 1;
            check(AtkProtocol.InspectReply(report, AtkReadCommand.Battery, out _) == AtkReplyError.DataLength);
            report[5] = 4;
            check(AtkProtocol.InspectReply(report, AtkReadCommand.Battery, out observation) == AtkReplyError.None
                && observation is { RawVoltageHigh: 16, RawVoltageLow: 19 });
        });
        run("AT02", "ATK rejects receiver/interface ambiguity and incomplete metadata without opening telemetry", () =>
        {
            check(AtkHidPolicy.Allows(Descriptor) && !AtkHidPolicy.Allows(Descriptor with { UsagePage = 0xFF04 })
                && !AtkHidPolicy.Allows(Descriptor with { OutputReport8 = false }) && !AtkHidPolicy.Allows(Descriptor with { InterfaceNumber = 2 }));
            var fake = new Transport { Endpoints = [new("private-path", Descriptor with { OutputBytes = 0 })] };
            var session = new AtkManualSession(fake);
            check(fake.Scans == 0 && fake.Opens == 0 && !session.TryRead());
            check(session.TryInspect() && session.State.CandidateCount == 0 && !session.TryRead() && fake.Opens == 0);
            session.StopAsync().GetAwaiter().GetResult();
            fake = new() { Endpoints = [new("private-path-1", Descriptor), new("private-path-2", Descriptor)] };
            session = new(fake); session.TryInspect();
            check(session.State.Status == AtkManualStatus.AmbiguousInterfaces && !session.TryRead() && fake.Opens == 0);
            session.StopAsync().GetAwaiter().GetResult();
            fake = new() { Errors = 1 }; session = new(fake); session.TryInspect();
            check(!session.TryRead() && fake.Opens == 0); session.StopAsync().GetAwaiter().GetResult();
        });
        run("AT03", "ATK observes online/model/battery in order, stops early, and keeps observations outside current battery", () =>
        {
            var fake = new Transport(); var time = new Clock(); var session = new AtkManualSession(fake, time);
            check(session.TryInspect() && session.TryRead());
            check(fake.Connection.Commands.SequenceEqual(new[] { AtkReadCommand.WirelessOnline, AtkReadCommand.MouseIdentity, AtkReadCommand.Battery })
                && session.State.Status == AtkManualStatus.ObservationOnly && session.State.AcceptedPercent is null
                && session.State.Observations.Last().PercentCandidate == 73 && fake.Connection.Disposals == 1);
            check(!JsonSerializer.Serialize(session.State).Contains("private-path") && !JsonSerializer.Serialize(fake.Endpoints).Contains("private-path"));
            check(!session.TryRead()); time.Advance(10); check(session.TryRead()); session.StopAsync().GetAwaiter().GetResult();
            fake = new() { Connection = new() { Online = false } }; session = new(fake); session.TryInspect(); session.TryRead();
            check(session.State.Status == AtkManualStatus.OfflineObservation && fake.Connection.Commands.Count == 1);
            session.StopAsync().GetAwaiter().GetResult();
            fake = new() { Connection = new() { Model = false } }; session = new(fake); session.TryInspect(); session.TryRead();
            check(session.State.Status == AtkManualStatus.DifferentModel && !fake.Connection.Commands.Contains(AtkReadCommand.Battery));
            session.StopAsync().GetAwaiter().GetResult();
        });
        run("AT04", "ATK timeout preserves original occupancy, blocks retries/late values, and reports bounded shutdown", () =>
        {
            var pending = new TaskCompletionSource<AtkReplyObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = new Transport { Connection = new() { Pending = pending.Task } }; var time = new Clock();
            var session = new AtkManualSession(fake, time); session.TryInspect(); check(session.TryRead());
            time.Advance(10); session.Tick();
            for (int i = 0; i < 5; i++) check(!session.TryRead() && !session.TryInspect());
            check(session.State is { Busy: true, Quarantined: true, Status: AtkManualStatus.TimedOut } && fake.Opens == 1);
            var shutdown = session.StopAsync().GetAwaiter().GetResult();
            check(!shutdown.FullyStopped && shutdown.UnresolvedOperations == 1 && fake.Connection.Disposals == 0);
            pending.SetResult(new(AtkReadCommand.WirelessOnline, 0, 1, true, OnlineCandidate: true));
            Wait(() => !session.State.Busy);
            check(session.State.Observations.Count == 0 && session.State.AcceptedPercent is null && fake.Connection.Disposals == 1
                && session.State.RequestsStarted == session.State.RequestsTerminal);
        });
    }

    private static byte[] Reply(AtkReadCommand command, int percent)
    {
        byte[] report = new byte[17]; report[0] = 8; report[1] = (byte)command;
        report[5] = 4; report[6] = (byte)percent; report[7] = 1;
        report[16] = unchecked((byte)(0x55 - report.Sum(b => b)));
        return report; // Synthetic response, not a captured device frame or a confirmed response convention.
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void Advance(int seconds) => _ticks += seconds * 1000;
    }
    private sealed class Transport : IAtkHidTransport
    {
        public AtkHidEndpoint[] Endpoints = [new("private-path", Descriptor)];
        public int Scans, Opens, Errors; public Connection Connection = new();
        public Task<AtkHidScan> ScanAsync(CancellationToken cancellation) { Scans++; return Task.FromResult(new AtkHidScan(Endpoints, Errors)); }
        public Task<IAtkHidConnection> OpenAsync(AtkHidEndpoint endpoint, CancellationToken cancellation) { Opens++; return Task.FromResult<IAtkHidConnection>(Connection); }
    }
    private sealed class Connection : IAtkHidConnection
    {
        public bool Online = true, Model = true; public int Disposals;
        public Task<AtkReplyObservation>? Pending;
        public List<AtkReadCommand> Commands = [];
        public Task<AtkReplyObservation> QueryAsync(AtkReadCommand command, CancellationToken cancellation)
        {
            Commands.Add(command);
            return Pending ?? Task.FromResult(new AtkReplyObservation(command, 0, 4, true,
                command == AtkReadCommand.WirelessOnline ? Online : null, command == AtkReadCommand.MouseIdentity ? Model : null,
                command == AtkReadCommand.Battery ? 73 : null));
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
