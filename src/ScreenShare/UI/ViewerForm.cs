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
    private readonly object _frameGate = new();
    private Bitmap? _pendingFrame;
    private bool _frameUpdateQueued;

    protected override bool ShowWithoutActivation => true; // 打开时不抢焦点

    public ViewerForm(string studentName)
    {
        // 布局按 96 DPI 基线书写，按真实 DPI 手动缩放
        float s = DpiScale.Factor(this);
        Text = $"正在观看老师的屏幕（{studentName}）";
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        int w = Math.Min((int)(960 * s), area.Width - 80);
        int h = Math.Min((int)(560 * s), area.Height - 80);
        Size = new Size(w, h);
        Location = new Point(area.Right - w - 24, area.Bottom - h - 24);
        MinimumSize = new Size((int)(320 * s), (int)(240 * s));
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

    /// <summary>由解码线程提交画面；界面忙时只保留最新一帧。</summary>
    public void QueueFrame(Bitmap frame)
    {
        Bitmap? old;
        bool schedule = false;
        lock (_frameGate)
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
            {
                old = frame;
            }
            else
            {
                old = _pendingFrame;
                _pendingFrame = frame;
                if (!_frameUpdateQueued)
                {
                    _frameUpdateQueued = true;
                    schedule = true;
                }
            }
        }
        old?.Dispose();
        if (!schedule) return;
        try { BeginInvoke((Action)ShowPendingFrame); }
        catch (ObjectDisposedException) { DropPendingFrame(); }
        catch (InvalidOperationException) { DropPendingFrame(); }
    }

    private void ShowPendingFrame()
    {
        Bitmap? frame;
        lock (_frameGate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _frameUpdateQueued = false;
        }
        if (frame is not null) ShowFrame(frame);
    }

    private void DropPendingFrame()
    {
        Bitmap? frame;
        lock (_frameGate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _frameUpdateQueued = false;
        }
        frame?.Dispose();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DropPendingFrame();
            var old = _box.Image;
            _box.Image = null;
            old?.Dispose();
        }
        base.Dispose(disposing);
    }
}
