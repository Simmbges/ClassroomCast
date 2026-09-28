namespace ScreenShare.UI;

/// <summary>
/// 学生观看窗口：自由调整大小、可最小化、可与其他窗口并排使用；
/// 打开时不抢占焦点，不置顶，不限制学生操作自己的电脑。
/// </summary>
public sealed class ViewerForm : Form
{
    private readonly PictureBox _box = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom, // 保持原始宽高比
        BackColor = Color.FromArgb(16, 16, 16),
    };

    private readonly Label _hint = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.Gainsboro,
        BackColor = Color.FromArgb(16, 16, 16),
        Font = new Font("Microsoft YaHei UI", 12F),
        Text = "正在接收老师的屏幕画面…",
    };

    protected override bool ShowWithoutActivation => true; // 打开时不抢焦点

    public ViewerForm(string studentName)
    {
        Text = $"正在观看老师的屏幕（{studentName}）";
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(Math.Min(960, area.Width - 80), Math.Min(560, area.Height - 80));
        Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
        MinimumSize = new Size(320, 240);
        Controls.Add(_hint);
        Controls.Add(_box);
        _box.BringToFront();
        _hint.Visible = true;
    }

    /// <summary>显示一帧。必须在 UI 线程调用；旧帧立即释放。</summary>
    public void ShowFrame(Bitmap frame)
    {
        if (IsDisposed) { frame.Dispose(); return; }
        var old = _box.Image;
        _box.Image = frame;
        old?.Dispose();
        if (_hint.Visible) { _hint.Visible = false; _box.BringToFront(); }
    }

    /// <summary>在画面位置显示提示文字（等待画面、连接断开等）。</summary>
    public void ShowMessage(string message)
    {
        if (IsDisposed) return;
        _hint.Text = message;
        var old = _box.Image;
        _box.Image = null;
        old?.Dispose();
        _hint.Visible = true;
        _hint.BringToFront();
    }
}
