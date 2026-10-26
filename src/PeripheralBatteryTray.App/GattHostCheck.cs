using System.Security.Cryptography;
using System.Text.Json;
using PeripheralBatteryTray.Core;
using PeripheralBatteryTray.Windows;

namespace PeripheralBatteryTray.App;

/// <summary>Opt-in manual UI only. Opening this window never starts a GATT read.</summary>
internal static class GattHostCheck
{
    internal static int Run(TrayApplicationContext context, WindowsBatteryRuntime runtime, string path)
    {
        var telemetry = new DiagnosticTelemetry();
        var client = new ObservedGattClient(new WinRtGattClient(action => context.Popup.BeginInvoke(action)), telemetry);
        var provider = new BleGattProvider(runtime.Bindings, client);
        bool Ready() => runtime.Discovery.BleStatus == DiscoveryStatus.Completed;
        var session = new ManualGattSession(runtime.Bindings, provider, Ready, telemetry);
        context.AdditionalShutdown = session.StopAsync;
        using var form = new Form { Text = "标准蓝牙电量 · 手动验证", StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new(600, 380), MinimumSize = new(500, 350), AutoScaleMode = AutoScaleMode.Dpi };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), RowCount = 5, ColumnCount = 1 };
        for (int i = 0; i < 4; i++) layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(550, 0), Text =
            "仅列出已配对、BLE 在线且有电池服务线索的设备。\n点击“读取一次”才会尝试标准电量读取，可能触发 Windows 访问提示或蓝牙通信。\n不会配对、写入设备或自动启用后台 GATT。" });
        var choices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        layout.Controls.Add(choices);
        var read = new Button { Text = "读取一次", AutoSize = true, Enabled = false };
        layout.Controls.Add(read);
        var note = new Label { AutoSize = true, Text = "正在读取现有设备列表；尚未发起 GATT。", MaximumSize = new(550, 0) };
        layout.Controls.Add(note);
        var results = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        layout.Controls.Add(results); form.Controls.Add(layout);
        string signature = "";
        using var timer = new System.Windows.Forms.Timer { Interval = 250 };
        void Update()
        {
            session.Tick();
            var state = session.State;
            var candidates = Ready() ? runtime.Bindings.Snapshot().Where(b => b.GattCandidate
                && b.Device.Binding.Ble?.Connection == ConnectionState.Connected
                && b.Endpoints.Count(e => e.Transport == TransportKind.Ble && e.Paired && e.Present
                    && e.Connection == ConnectionState.Connected) == 1).ToArray() : [];
            string next = string.Join("|", candidates.Select(b => b.Device.Binding.BindingKey + ":" + b.Device.DisplayName));
            if (!state.Busy && next != signature)
            {
                string? selected = (choices.SelectedItem as Choice)?.Key;
                choices.Items.Clear();
                foreach (var binding in candidates) choices.Items.Add(new Choice(binding.Device.Binding.BindingKey, binding.Device.DisplayName));
                choices.SelectedIndex = choices.Items.Cast<Choice>().Select((c, i) => (c, i))
                    .Where(x => x.c.Key == selected).Select(x => x.i).DefaultIfEmpty(choices.Items.Count > 0 ? 0 : -1).First();
                signature = next;
            }
            choices.Enabled = !state.Busy;
            read.Enabled = !state.Busy && state.RetryAfterSeconds <= 0 && choices.SelectedItem is Choice && !context.IsExiting;
            note.Text = state.Busy ? state.TimedOut ? "读取已超时，等待原始调用收尾；不会叠加新请求。" : "读取中，可关闭窗口取消。"
                : state.Attempts > 0 ? $"已执行 {state.Attempts} 次；结果：{state.Outcome}。两次读取至少间隔10秒。"
                : candidates.Length == 0 ? "未发现符合条件的在线 BLE 设备；尚未发起 GATT。" : "请选择设备并点击读取；尚未发起 GATT。";
            results.Text = string.Join(Environment.NewLine, state.Values.Select((v, i) =>
                $"最近一次读取 · 分量 {i + 1}：{(v.Percent is int percent ? percent + "%" : v.Outcome.ToString())} · {v.ObservedAtUtc:HH:mm:ss} · GATT"));
        }
        read.Click += (_, _) => { if (choices.SelectedItem is Choice selected && !session.TryRead(selected.Key))
            note.Text = "当前不可读取：请等待10秒，或确认设备仍满足在线条件。"; Update(); };
        timer.Tick += (_, _) => Update();
        form.FormClosed += (_, _) => context.RequestExit();
        context.ThreadExit += (_, _) => form.Close();
        form.Show(); timer.Start();
        Application.Run(context); timer.Stop();
        var final = session.State;
        string output = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var files = new[] { typeof(GattHostCheck).Assembly.Location, typeof(BatteryRules).Assembly.Location, typeof(BleGattProvider).Assembly.Location };
        bool readSucceeded = final.Values.Any(v => v.Outcome == ReadOutcome.Success && v.Percent is not null);
        bool passed = readSucceeded && !final.TimedOut && context.Shutdown?.FullyStopped == true;
        File.WriteAllText(output, JsonSerializer.Serialize(new { BuildId = BuildInfo.Id, TimestampUtc = DateTimeOffset.UtcNow,
            Runtime = Environment.Version.ToString(), Passed = passed, Status = final.Attempts == 0 ? "NotRun" : passed ? "ReadSucceeded" : "NeedsReview",
            ManualOnly = true, ServiceUuid = BatteryGattUuids.Service, CharacteristicUuid = BatteryGattUuids.Level,
            ReadCacheMode = "Uncached", SystemAccessPrompt = "Requires operator observation; not inferred from API success",
            Result = final, Shutdown = context.Shutdown,
            Assemblies = files.Select(f => new { File = Path.GetFileName(f), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) }),
            Scope = "Manual BAS/Uncached reads only; no pairing, writes or automatic GATT. Success does not establish V1-V6, per-device compatibility or 24-hour acceptance. No device identities exported." },
            new JsonSerializerOptions { WriteIndented = true }));
        return final.Attempts == 0 ? 2 : passed ? 0 : 1;
    }
    private sealed record Choice(string Key, string Name) { public override string ToString() => Name; }
}
