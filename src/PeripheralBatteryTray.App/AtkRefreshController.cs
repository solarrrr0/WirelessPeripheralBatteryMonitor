using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal enum AtkRefreshState { ManualOnly, Scheduled, Reading, Backoff, Blocked, Draining, Suspended, Stopped }

/// <summary>UI-owned scheduling. Timer callbacks only post versioned work to the UI dispatcher.</summary>
internal class AtkRefreshController
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    private readonly AtkBatterySession _session;
    private readonly TimeProvider _time;
    private Action<Action>? _post;
    private ITimer? _timer;
    private int _timerVersion, _epoch, _failures;
    private bool _enabled, _closed, _suspended, _reading, _valid, _automatic, _attemptAutomatic;
    private bool _offline;
    private int _minutes = 5;
    private long? _due, _retryFloor, _lastStart;
    private long _captured;
    private long _acceptedSequence;
    private int? _lastPercent;
    private DateTimeOffset? _observed;
    private string _status = "尚未读取；点击“读取 ATK 电量”查询一次。";
    internal event Action? Changed;
    internal Task ActiveTask { get; private set; } = Task.CompletedTask;
    internal AtkRefreshController(IAtkHidTransport transport, TimeProvider? time = null)
    { _time = time ?? TimeProvider.System; _session = new(transport, _time); }
    private long Now => _time.GetTimestamp();
    private long After(TimeSpan delay) => Now + (long)Math.Ceiling(delay.TotalSeconds * _time.TimestampFrequency);
    private TimeSpan Until(long timestamp) => TimeSpan.FromSeconds(Math.Max(0, (timestamp - Now) / (double)_time.TimestampFrequency));
    internal bool AutoEnabled => _enabled;
    internal int IntervalMinutes => _minutes;
    internal AtkRefreshState State => _closed ? AtkRefreshState.Stopped : _suspended ? AtkRefreshState.Suspended
        : _session.State is { Busy: true, Invalidated: true } ? AtkRefreshState.Draining
        : _session.State.Blocked ? AtkRefreshState.Blocked : _reading ? AtkRefreshState.Reading
        : !_enabled ? AtkRefreshState.ManualOnly : _failures > 0 ? AtkRefreshState.Backoff : AtkRefreshState.Scheduled;
    internal string AutomaticStatus => "ATK 自动刷新：" + (State switch
    {
        AtkRefreshState.Stopped => "已停止", AtkRefreshState.Suspended => "已暂停",
        AtkRefreshState.Draining => "等待查询收尾", AtkRefreshState.Blocked => "已阻断，请退出重启",
        _ when !_enabled => "关闭",
        AtkRefreshState.Reading => "读取中", AtkRefreshState.Backoff => "等待重试",
        _ => "已开启"
    }) + (_enabled && _due is long due && State is AtkRefreshState.Scheduled or AtkRefreshState.Backoff
        ? $"（约 {Math.Ceiling(Until(due).TotalSeconds)} 秒后）" : "");
    internal TimeSpan RemainingFreshness => _valid ? Until(_captured + (long)(Freshness.TotalSeconds * _time.TimestampFrequency)) : TimeSpan.Zero;
    internal AtkTrayReading Reading
    {
        get
        {
            bool fresh = _valid && !_closed && !_suspended && _time.GetElapsedTime(_captured) < Freshness;
            bool busy = _reading || _session.State.Busy;
            return new(fresh ? _lastPercent : null, _lastPercent, _observed,
                _valid && !fresh ? "上次读数已过期。" : _status, busy,
                !_closed && !_suspended && !busy && !_session.State.Blocked, _automatic, AutomaticStatus, _acceptedSequence,
                !fresh && !busy && !_suspended && _offline);
        }
    }
    internal void AttachDispatcher(Action<Action> post)
    {
        if (_post is not null) throw new InvalidOperationException("ATK dispatcher already attached.");
        _post = post; Rearm();
    }
    internal void Configure(bool enabled, int minutes)
    {
        if (minutes is < 5 or > 30) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (_closed) return;
        bool toggled = _enabled != enabled, changed = _minutes != minutes;
        _enabled = enabled; _minutes = minutes;
        if (!enabled)
        {
            _due = null;
            if (toggled && _reading) { _epoch++; _valid = false; _session.Cancel(); }
        }
        else if (toggled || changed)
            _due = NextAllowed(After(toggled ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(minutes)));
        Notify();
    }
    private long NextAllowed(long requested) => Math.Max(requested,
        Math.Max(_retryFloor ?? long.MinValue, _lastStart is long start ? start + 10 * _time.TimestampFrequency : long.MinValue));
    internal bool TryRead() => Start(false);
    private bool Start(bool automatic)
    {
        if (!Reading.CanRead || (automatic && !_enabled)) return false;
        if (_lastStart is long last && _time.GetElapsedTime(last) < TimeSpan.FromSeconds(10))
        { _status = "请等待10秒查询间隔结束后再读。"; Notify(); return false; }
        _reading = true; _valid = false; _offline = false; _attemptAutomatic = automatic; _lastStart = Now; _due = null;
        _status = "正在检查接收器并读取电量…";
        if (!_session.TryStart()) { _reading = false; Notify(); return false; }
        ActiveTask = ObserveAsync(_epoch);
        Notify(); return true;
    }
    private async Task ObserveAsync(int epoch)
    {
        var result = await _session.ActiveTask;
        _reading = false;
        if (_closed) return;
        if (epoch == _epoch && !_suspended)
        {
            _offline = result.Outcome is AtkBatchOutcome.Offline or AtkBatchOutcome.NoInterface;
            if (result.Outcome == AtkBatchOutcome.Success && !_session.State.Blocked)
            {
                _lastPercent = result.Percent; _captured = result.CapturedTimestamp; _observed = result.ObservedAtUtc;
                _automatic = _attemptAutomatic;
                _acceptedSequence++;
                _valid = true; _failures = 0; _retryFloor = null;
                _status = "接收器读取；充电状态未知。";
                if (_enabled) _due = NextAllowed(After(TimeSpan.FromMinutes(_minutes)));
            }
            else
            {
                _status = OutcomeText(result.Outcome);
                if (!_session.State.Blocked)
                {
                    _failures = Math.Min(_failures + 1, 8);
                    _retryFloor = After(TimeSpan.FromMinutes(Math.Min(30, _minutes * (1 << (_failures - 1)))));
                    if (_enabled) _due = NextAllowed(_retryFloor.Value);
                }
            }
        }
        else if (_enabled && !_suspended && !_session.State.Blocked)
            _due = NextAllowed(_due ?? After(TimeSpan.FromSeconds(10)));
        Notify();
    }
    private static string OutcomeText(AtkBatchOutcome outcome) => outcome switch
    {
        AtkBatchOutcome.NoInterface => "未发现匹配的 ATK 接收器。",
        AtkBatchOutcome.Offline => "本次鼠标未在线，保留上次读数供参考。",
        AtkBatchOutcome.TransientFailure => "接口检查或打开失败，可能被占用；等待重试。",
        AtkBatchOutcome.DifferentModel => "接收器下的鼠标型号不匹配 Duckbill Ultra，请退出重启。",
        AtkBatchOutcome.AmbiguousInterfaces => "存在多个接收器接口，无法唯一选择，请退出重启。",
        AtkBatchOutcome.TimedOut => "查询超时；可能已发送请求时停止后续查询，请退出重启。",
        AtkBatchOutcome.Cancelled => "查询已取消；可能已发送请求时停止后续查询，请退出重启。",
        _ => "回复或清理未通过；本次没有有效电量，请退出重启。"
    };
    private void Notify() { Rearm(); Changed?.Invoke(); }
    private void Rearm()
    {
        int version = ++_timerVersion;
        _timer?.Dispose(); _timer = null;
        if (_post is null || _closed || _suspended) return;
        TimeSpan? delay = null;
        void Consider(TimeSpan value) { if (delay is null || value < delay) delay = value; }
        var batch = _session.State;
        if (batch.Busy && !batch.Invalidated) Consider(batch.Remaining);
        if (_valid && RemainingFreshness > TimeSpan.Zero) Consider(RemainingFreshness);
        if (_enabled && !_reading && !batch.Busy && !batch.Blocked && _due is long due) Consider(Until(due));
        if (delay is not TimeSpan wait) return;
        _timer = _time.CreateTimer(_ => _post(() => { if (!_closed && version == _timerVersion) Tick(); }),
            null, wait, Timeout.InfiniteTimeSpan);
    }
    internal void Tick()
    {
        if (_closed || _suspended) return;
        _session.Tick();
        if (_valid && RemainingFreshness == TimeSpan.Zero) { _valid = false; _status = "上次读数已过期。"; }
        if (_session.State.Invalidated && _session.State.Busy) _status = OutcomeText(_session.State.Outcome);
        if (_enabled && !_reading && !_session.State.Busy && !_session.State.Blocked && _due is long due && Now >= due)
        { if (Start(true)) return; }
        Notify();
    }
    internal void Suspend()
    {
        if (_closed || _suspended) return;
        _suspended = true; _epoch++; _valid = false; _offline = false; _session.Cancel();
        _status = "系统已暂停，旧读数仅供参考。"; Notify();
    }
    internal void Resume()
    {
        if (_closed || !_suspended) return;
        _suspended = false; _status = "系统已恢复，等待新的电量读取。";
        if (_enabled && !_session.State.Blocked) _due = NextAllowed(After(TimeSpan.FromSeconds(10)));
        Notify();
    }
    internal AtkDiagnosticSummary CaptureDiagnostics()
    {
        var s = _session.State; var r = Reading;
        return new(_enabled, _minutes, State.ToString(), s.Outcome.ToString(), s.Batches, s.BatchesTerminal,
            s.RequestsStarted, s.RequestsTerminal, s.Timeouts, s.CleanupErrors, r.Percent, r.LastPercent, r.ObservedAtUtc);
    }
    internal Task<ShutdownResult> StopAsync()
    {
        _closed = true; _valid = false; _epoch++; _due = null;
        _timerVersion++; _timer?.Dispose(); _timer = null;
        return _session.StopAsync();
    }
}
