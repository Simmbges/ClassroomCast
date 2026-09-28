using ScreenShare.Core;

namespace ScreenShare.UI;

/// <summary>学生端面板：姓名/IP 输入、连接/断开、观看窗口管理。</summary>
public sealed class ClientPanel : UserControl
{
    private readonly TextBox _txtName = new() { Left = 110, Top = 12, Width = 295 };
    private readonly TextBox _txtIp = new() { Left = 110, Top = 47, Width = 295 };
    private readonly NumericUpDown _numPort = new() { Left = 110, Top = 82, Width = 90, Minimum = 1024, Maximum = 65535, Value = 9527 };
    private readonly Button _btnConnect = new() { Text = "Client - 连接", Left = 20, Top = 122, Width = 200, Height = 38 };
    private readonly Label _lblStatus = new() { Left = 236, Top = 130, Width = 250, Height = 24, Text = "状态：未连接", ForeColor = Color.DimGray };
    private readonly Label _lblFps = new() { Left = 236, Top = 154, Width = 250, Height = 20, Text = "", ForeColor = Color.DimGray };

    private StreamClient? _client;
    private ViewerForm? _viewer;
    private bool _connecting;

    public ClientPanel()
    {
        Size = new Size(510, 520);

        Controls.Add(new Label { Text = "我的姓名：", Left = 20, Top = 16, AutoSize = true });
        Controls.Add(_txtName);
        Controls.Add(new Label { Text = "老师 IP：", Left = 20, Top = 51, AutoSize = true });
        Controls.Add(_txtIp);
        Controls.Add(new Label { Text = "端口：", Left = 20, Top = 86, AutoSize = true });
        Controls.Add(_numPort);
        Controls.Add(_btnConnect);
        Controls.Add(_lblStatus);
        Controls.Add(_lblFps);

        var grp = new GroupBox { Text = "使用提示", Left = 20, Top = 178, Width = 460, Height = 150 };
        grp.Controls.Add(new Label
        {
            Left = 12, Top = 24, Width = 436, Height = 118,
            Text = "1. 姓名会显示在老师端的在线名单里，同名同学会自动编号区分。\r\n" +
                   "2. 向老师询问老师电脑上显示的 IP 地址和端口。\r\n" +
                   "3. 点击连接后会自动打开观看窗口，画面随窗口大小保持比例缩放。\r\n" +
                   "4. 观看窗口可以最小化、移动或与其他软件并排使用，不影响你\r\n    正常操作自己的电脑。",
            ForeColor = Color.DimGray,
        });
        Controls.Add(grp);

        _btnConnect.Click += async (_, _) => await ToggleAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _viewer?.Dispose();
            _client?.Disconnect();
        }
        base.Dispose(disposing);
    }

    private bool IsConnected => _client is { IsConnected: true };

    private async Task ToggleAsync()
    {
        if (_connecting) return;
        if (IsConnected)
        {
            Disconnect("已主动断开连接", userInitiated: true);
            return;
        }

        // 输入校验（立即反馈，具体网络错误由连接过程给出中文提示）
        if (string.IsNullOrWhiteSpace(_txtName.Text))
        {
            MessageBox.Show(this, "请先输入你的姓名。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_txtIp.Text))
        {
            MessageBox.Show(this, "请先输入老师电脑的 IP 地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtIp.Focus();
            return;
        }

        _connecting = true;
        SetUiConnecting(true);
        SetStatus("正在连接老师电脑…", Color.DimGray);

        _client = new StreamClient();
        WireEvents(_client);
        try
        {
            await _client.ConnectAsync(_txtIp.Text.Trim(), (int)_numPort.Value, _txtName.Text.Trim(), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SetUiConnecting(false);
            _client = null;
            SetStatus("连接已取消", Color.DimGray);
            return;
        }
        catch (Exception ex)
        {
            SetUiConnecting(false);
            _client = null;
            SetStatus("连接失败", Color.Firebrick);
            MessageBox.Show(this, ex.Message, "无法连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _connecting = false;
        SetUiConnecting(false);
        _btnConnect.Text = "Client - 断开";
        SetStatus("已连接，等待老师开始共享…", Color.DimGray);
        OpenViewer();
    }

    private void WireEvents(StreamClient client)
    {
        client.StatusChanged += msg => RunOnUi(() => SetStatus(msg, Color.ForestGreen));
        client.FpsChanged += fps => RunOnUi(() =>
            _lblFps.Text = fps >= 0 ? $"当前画面约 {fps} FPS" : "");
        client.ConnectionLost += reason => RunOnUi(() =>
        {
            _viewer?.ShowMessage("连接已断开\n" + reason);
            Disconnect(reason, userInitiated: false);
        });
        client.FrameDecoded += frame =>
        {
            var viewer = _viewer;
            if (viewer is null || viewer.IsDisposed || !viewer.IsHandleCreated)
            {
                frame.Dispose();
                return;
            }
            try { viewer.BeginInvoke(() => viewer.ShowFrame(frame)); }
            catch (ObjectDisposedException) { frame.Dispose(); }
            catch (InvalidOperationException) { frame.Dispose(); }
        };
    }

    private void OpenViewer()
    {
        if (_viewer is { IsDisposed: false }) return;
        _viewer = new ViewerForm(_txtName.Text.Trim());
        _viewer.FormClosed += (_, _) =>
        {
            // 关闭观看窗口即断开观看（重新连接即可再次观看）
            if (IsConnected) Disconnect("已关闭观看窗口", userInitiated: true);
            _viewer = null;
        };
        _viewer.Show(); // ShowWithoutActivation 生效，不抢学生焦点
    }

    private void Disconnect(string reason, bool userInitiated)
    {
        _client?.Disconnect();
        _client = null;
        _lblFps.Text = "";
        _btnConnect.Text = "Client - 连接";
        SetStatus(reason, userInitiated ? Color.DimGray : Color.Firebrick);
    }

    private void SetStatus(string text, Color color)
    {
        _lblStatus.Text = "状态：" + text;
        _lblStatus.ForeColor = color;
    }

    private void SetUiConnecting(bool connecting)
    {
        _btnConnect.Enabled = !connecting;
        if (connecting) _btnConnect.Text = "连接中…";
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
