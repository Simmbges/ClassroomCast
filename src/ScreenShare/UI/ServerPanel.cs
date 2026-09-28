using System.Net.Sockets;
using ScreenShare.Core;

namespace ScreenShare.UI;

/// <summary>老师端面板：地址选择、开始/停止共享、帧率、在线学生名单与日志。</summary>
public sealed class ServerPanel : UserControl
{
    private static readonly int[] FpsChoices = [5, 10, 15, 30, 60];

    private readonly StreamServer _server = new();

    private readonly ComboBox _cboAddress = new() { DropDownStyle = ComboBoxStyle.DropDownList, Left = 110, Top = 14, Width = 230 };
    private readonly Button _btnCopyIp = new() { Text = "复制 IP", Left = 348, Top = 12, Width = 72 };
    private readonly Button _btnRefresh = new() { Text = "刷新", Left = 428, Top = 12, Width = 58 };
    private readonly NumericUpDown _numPort = new() { Left = 110, Top = 50, Width = 90, Minimum = 1024, Maximum = 65535, Value = 9527 };
    private readonly ComboBox _cboFps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Left = 300, Top = 50, Width = 90 };
    private readonly Button _btnToggle = new() { Text = "Server - 开始共享", Left = 20, Top = 90, MinimumSize = new Size(200, 0), AutoSize = true };
    private readonly Label _lblStatus = new() { Left = 236, Top = 98, Width = 250, Height = 24, Text = "状态：未共享", ForeColor = Color.DimGray };
    private readonly ListView _lstStudents = new() { View = View.Details, FullRowSelect = true, HideSelection = true };
    private readonly GroupBox _grpStudents = new() { Text = "在线学生（0）", Left = 20, Top = 140, Width = 460, Height = 170 };
    private readonly ListBox _lstLog = new() { Left = 20, Top = 330, Width = 460, Height = 170, IntegralHeight = false };
    private readonly System.Windows.Forms.Timer _copyResetTimer = new() { Interval = 1500 };

    public ServerPanel()
    {
        // 布局数值按 96 DPI 基线书写；自身 Size 也保持基线，由父窗体统一缩放
        float s = DpiScale.Factor(this);
        Size = new Size(510, 520);

        Controls.Add(new Label { Text = "本机地址：", Left = 20, Top = 18, AutoSize = true });
        Controls.Add(_cboAddress);
        Controls.Add(_btnCopyIp);
        Controls.Add(_btnRefresh);

        Controls.Add(new Label { Text = "端口：", Left = 20, Top = 53, AutoSize = true });
        Controls.Add(_numPort);
        Controls.Add(new Label { Text = "帧率 FPS：", Left = 218, Top = 53, AutoSize = true });
        foreach (var f in FpsChoices) _cboFps.Items.Add(f);
        _cboFps.SelectedItem = 15;
        Controls.Add(_cboFps);

        Controls.Add(_btnToggle);
        Controls.Add(_lblStatus);

        _lstStudents.Columns.Add("学生姓名", 170);
        _lstStudents.Columns.Add("连接时间", 230);
        _lstStudents.Left = 12; _lstStudents.Top = 26;
        _lstStudents.Width = _grpStudents.Width - 24;
        _lstStudents.Height = _grpStudents.Height - 34;
        _grpStudents.Controls.Add(_lstStudents);
        Controls.Add(_grpStudents);

        Controls.Add(new Label { Text = "日志：", Left = 20, Top = 312, AutoSize = true });
        Controls.Add(_lstLog);

        _btnRefresh.Click += (_, _) => RefreshAddresses();
        _btnCopyIp.Click += (_, _) => CopyIpToClipboard();
        _btnToggle.Click += async (_, _) => await ToggleAsync();
        _cboFps.SelectedIndexChanged += (_, _) =>
        {
            if (_server.IsStreaming)
                _server.StartStreaming((int)_cboFps.SelectedItem!);
        };

        _server.Log += msg => RunOnUi(() => AddLog(msg));
        _server.ClientsChanged += () => RunOnUi(RefreshStudents);
        _server.StatsUpdated += (encFps, sentFps, totalBytes) =>
            RunOnUi(() => UpdateStats(encFps, sentFps, totalBytes));
        _copyResetTimer.Tick += (_, _) =>
        {
            _copyResetTimer.Stop();
            _btnCopyIp.Text = "复制 IP";
        };

        DpiScale.ScaleChildren(this, s);
        RefreshAddresses();
    }

    /// <summary>供测试检查地址枚举结果。</summary>
    public int AddressCount => _cboAddress.Items.Count;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.Stop();
            _copyResetTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>把当前选中的本机 IP 复制到剪贴板，按钮短暂显示"已复制 ✓"作为反馈。</summary>
    private void CopyIpToClipboard()
    {
        if (_cboAddress.SelectedItem is not NetworkUtils.NicAddress nic)
        {
            MessageBox.Show(this, "当前没有可复制的 IP 地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Clipboard.SetText(nic.Address.ToString());
            AddLog($"已复制 IP {nic.Address} 到剪贴板");
            _btnCopyIp.Text = "已复制 ✓";
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshAddresses()
    {
        var previous = _cboAddress.SelectedItem is NetworkUtils.NicAddress selected ? selected.Address.ToString() : null;
        _cboAddress.Items.Clear();
        var list = NetworkUtils.GetLocalIPv4Addresses();
        foreach (var nic in list) _cboAddress.Items.Add(nic);
        if (list.Count == 0)
        {
            _cboAddress.Items.Add("未检测到可用的局域网 IPv4 地址，请检查网络连接");
            _cboAddress.SelectedIndex = 0;
            return;
        }
        int idx = list.FindIndex(n => n.Address.ToString() == previous);
        if (idx < 0)
            idx = list.FindIndex(n => n.Address.ToString().StartsWith("192.168.")
                                   || n.Address.ToString().StartsWith("10.")
                                   || n.Address.ToString().StartsWith("172."));
        _cboAddress.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private async Task ToggleAsync()
    {
        if (_server.IsStreaming)
        {
            _server.StopStreaming();
            return;
        }

        if (_cboAddress.SelectedItem is not NetworkUtils.NicAddress nic)
        {
            MessageBox.Show(this, "请先选择本机的局域网 IP 地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int port = (int)_numPort.Value;
        int fps = (int)_cboFps.SelectedItem!;

        try
        {
            if (!_server.IsListening)
                _server.Start(nic.Address.ToString(), port);
        }
        catch (SocketException)
        {
            MessageBox.Show(this,
                $"端口 {port} 已被其他程序占用。\n请更换端口后重试，或关闭占用该端口的程序。",
                "端口被占用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动服务失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _server.StartStreaming(fps);
        await Task.CompletedTask;
    }

    private void RefreshStudents()
    {
        var details = _server.GetClientDetails();
        _lstStudents.BeginUpdate();
        _lstStudents.Items.Clear();
        foreach (var (name, at) in details)
            _lstStudents.Items.Add(new ListViewItem([name, at.ToString("HH:mm:ss")]));
        _lstStudents.EndUpdate();
        _grpStudents.Text = $"在线学生（{details.Count}）";
        if (!_server.IsStreaming)
            _lblStatus.Text = details.Count > 0 ? $"状态：已停止共享（{details.Count} 名学生仍连接）" : "状态：未共享";
    }

    private void UpdateStats(int encodeFps, int sentFps, long totalBytes)
    {
        if (_server.IsStreaming)
        {
            double mb = totalBytes / 1024.0 / 1024.0;
            _lblStatus.Text = $"状态：共享中｜编码 {encodeFps} FPS｜已发送 {mb:0.0} MB";
            _lblStatus.ForeColor = Color.ForestGreen;
        }
        else
        {
            _lblStatus.Text = _server.ClientCount > 0
                ? $"状态：已停止共享（{_server.ClientCount} 名学生仍连接）"
                : "状态：未共享";
            _lblStatus.ForeColor = Color.DimGray;
        }
    }

    private void AddLog(string message)
    {
        _lstLog.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_lstLog.Items.Count > 300) _lstLog.Items.RemoveAt(0);
        _lstLog.TopIndex = _lstLog.Items.Count - 1;
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
