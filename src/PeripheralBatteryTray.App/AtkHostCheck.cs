using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

internal static class AtkHostCheck
{
    // This separate host has no Bluetooth runtime or automatic device provider.
    internal static int Run(SingleInstance instance, string reportPath)
    {
        var session = new AtkManualSession(new Win32AtkHidTransport()); // Inert until a button is clicked.
        using var form = new Form { Text = "ATK Duckbill Ultra · 手动协议观测", StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new(650, 400), MinimumSize = new(550, 360), AutoScaleMode = AutoScaleMode.Dpi };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(600, 0), Text =
            "1. 检查接口只读取接收器描述信息。\n2. 点击“查询一次”会发送在线/型号/电量的 HID Output 查询请求。\n不配对、不修改固件、DPI或轮询率。请先关闭可能访问接收器的官网驱动。\n本窗口用于协议观测；日常使用可从正常托盘手动读取。" });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var inspect = new Button { Text = "检查接口", AutoSize = true };
        var read = new Button { Text = "查询一次", AutoSize = true, Enabled = false };
        var close = new Button { Text = "关闭并保存结果", AutoSize = true };
        buttons.Controls.AddRange([inspect, read, close]); layout.Controls.Add(buttons);
        var status = new Label { Text = "未检查，未发送设备查询。", AutoSize = true, MaximumSize = new(600, 0) };
        layout.Controls.Add(status);
        var results = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        layout.Controls.Add(results); form.Controls.Add(layout);
        bool closing = false, mayClose = false;
        ShutdownResult? shutdown = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 250 };
        void Update()
        {
            session.Tick(); var state = session.State;
            inspect.Enabled = !closing && !state.Busy && !state.Quarantined;
            read.Enabled = inspect.Enabled && state.CandidateCount == 1 && state.InspectionErrors == 0 && state.RetryAfterSeconds <= 0;
            status.Text = StatusText(state);
            string text = string.Join(Environment.NewLine, state.Observations.Select(o =>
                $"查询 0x{(byte)o.Command:X2}：状态原值 0x{o.RawStatus:X2}，长度字段 {o.DeclaredDataLength}，请求式校验匹配 {o.MatchesRequestChecksumConvention}"
                + (o.PercentCandidate is int p ? $"；候选电量 {p}%（待验证）" : "")));
            if (results.Text != text) results.Text = text;
        }
        inspect.Click += (_, _) => { session.TryInspect(); Update(); };
        read.Click += (_, _) => { session.TryRead(); Update(); };
        close.Click += (_, _) => form.Close();
        timer.Tick += (_, _) => Update();
        form.FormClosing += async (_, e) =>
        {
            if (mayClose) return;
            e.Cancel = true; if (closing) return;
            closing = true; inspect.Enabled = read.Enabled = close.Enabled = false; timer.Stop();
            status.Text = "正在取消并收尾…";
            shutdown = await session.StopAsync();
            mayClose = true; form.Close();
        };
        void Post(Action action)
        {
            if (closing || form.IsDisposed) return;
            try { form.BeginInvoke(() => { if (!closing) action(); }); }
            catch (InvalidOperationException) when (closing || form.IsDisposed) { }
        }
        var show = ThreadPool.RegisterWaitForSingleObject(instance.ShowEvent!, (_, _) => Post(() => { form.Show(); form.Activate(); }), null, Timeout.Infinite, false);
        var exit = ThreadPool.RegisterWaitForSingleObject(instance.ExitEvent!, (_, _) => Post(form.Close), null, Timeout.Infinite, false);
        var exact = ThreadPool.RegisterWaitForSingleObject(instance.ExactExitEvent!, (_, _) => Post(form.Close), null, Timeout.Infinite, false);
        _ = form.Handle;
        try { timer.Start(); Application.Run(form); }
        finally { timer.Stop(); show.Unregister(null); exit.Unregister(null); exact.Unregister(null); }
        var final = session.State;
        var files = new[] { Environment.ProcessPath!, typeof(AtkHostCheck).Assembly.Location,
            typeof(BatteryRules).Assembly.Location, typeof(AtkProtocol).Assembly.Location };
        string output = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            BuildId = BuildInfo.Id, TimestampUtc = DateTimeOffset.UtcNow, Runtime = Environment.Version.ToString(),
            Status = final.ReadBatches == 0 ? "NotRun" : final.Status.ToString(), HardwareValidated = false,
            Capability = "ATK manual protocol observation; default HID provider disabled", Result = final, Shutdown = shutdown,
            Assemblies = files.Select(f => new { File = Path.GetFileName(f), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) }),
            Scope = "Explicit metadata inspection and telemetry-only HID Output/Input requests. Candidate values only, not trusted current battery. Response status/checksum semantics and real compatibility remain unverified. No pairing/configuration/firmware commands, device paths, RF identifiers or serial numbers in this report."
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        return final.ReadBatches == 0 ? 2 : final.Status == AtkManualStatus.ObservationOnly && shutdown?.FullyStopped == true ? 0 : 1;
    }

    private static string StatusText(AtkManualState state)
    {
        if (state.Quarantined) return state.Busy ? "查询已停止，等待原始调用收尾。" : "本会话发生超时或协议异常，已停止重试；请保留报告。";
        return state.Status switch
        {
            AtkManualStatus.NotRun => "未检查，未发送设备查询。",
            AtkManualStatus.Inspecting => "正在检查精确接收器的接口与报告能力…",
            AtkManualStatus.InterfacesChecked => "接口检查完成，可手动查询一次；未发送查询。",
            AtkManualStatus.NoInterface => "未发现满足要求的接口；不会尝试其他 collection。",
            AtkManualStatus.AmbiguousInterfaces => "找到多个候选接口，无法唯一选择；不会发送查询。",
            AtkManualStatus.Reading => "正在查询在线状态、型号和电量…",
            AtkManualStatus.OfflineObservation => "在线状态候选为离线，本次未查询电量。",
            AtkManualStatus.DifferentModel => "型号响应不符合 Duckbill Ultra，本次未查询电量。",
            AtkManualStatus.ObservationOnly => "已记录候选观测；本工具未启用后台读取。",
            AtkManualStatus.NeedsProtocolReview => "接口检查或响应需要复核；不能作为当前电量。",
            _ => $"本次状态：{state.Status}。不会自动重试。"
        };
    }
}
