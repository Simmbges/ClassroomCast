using System.Runtime.InteropServices;

namespace ScreenShare.UI;

/// <summary>
/// 学生签到窗口：老师发起签到时弹出。不抢占学生当前焦点（任务栏闪烁提醒），
/// 学生填写姓名与学号提交；学号已被签到或签到已结束会给出红字提示并可修改重试；
/// 成功后自动关闭。倒计时归零或老师提前结束时停止提交并自动关闭。
/// </summary>
public sealed class SignInForm : Form
{
    /// <summary>学生点击提交（学号、姓名）。提交结果经 SetResult 回流。</summary>
    public event Action<string, string>? SubmitRequested;

    private readonly Label _lblTime = new()
    {
        Left = 70, Top = 46, AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
        ForeColor = Color.Firebrick,
    };
    private readonly TextBox _txtName = new() { Left = 70, Top = 88, Width = 220 };
    private readonly TextBox _txtId = new() { Left = 70, Top = 124, Width = 220 };
    private readonly Button _btnSubmit = new()
    {
        Text = "提交签到", Left = 70, Top = 162, Size = new Size(120, 30),
        Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
    };
    private readonly Label _lblResult = new()
    {
        Left = 70, Top = 200, AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
        ForeColor = Color.Firebrick,
    };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private int _remaining;
    private bool _succeeded;
    private bool _expired;

    protected override bool ShowWithoutActivation => true; // 不抢焦点，仅任务栏闪烁提醒

    public SignInForm(string prefillName, int remainingSeconds)
    {
        // 布局按 96 DPI 基线书写，按真实 DPI 手动缩放
        float s = DpiScale.Factor(this);
        Text = "课堂签到";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        ClientSize = new Size((int)(320 * s), (int)(240 * s));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (int)(40 * s));

        Controls.Add(new Label
        {
            Left = 16, Top = 14, AutoSize = true,
            Text = "老师发起了课堂签到，请填写以下信息：",
        });
        Controls.Add(_lblTime);
        Controls.Add(new Label { Left = 16, Top = 91, AutoSize = true, Text = "姓名：" });
        Controls.Add(_txtName);
        Controls.Add(new Label { Left = 16, Top = 127, AutoSize = true, Text = "学号：" });
        Controls.Add(_txtId);
        Controls.Add(_btnSubmit);
        Controls.Add(_lblResult);

        _txtName.Text = prefillName;
        _remaining = Math.Max(0, remainingSeconds);
        UpdateTimeLabel();

        _btnSubmit.Click += (_, _) => Submit();
        _timer.Tick += (_, _) => TickSecond();
        _timer.Start();
        DpiScale.ScaleChildren(this, s);

        AcceptButton = _btnSubmit; // 回车直接提交
    }

    private void Submit()
    {
        if (_succeeded || _expired) return;
        if (_txtId.Text.Trim().Length == 0)
        {
            SetResult(false, "请填写学号");
            _txtId.Focus();
            return;
        }
        SubmitRequested?.Invoke(_txtId.Text.Trim(), _txtName.Text.Trim());
    }

    /// <summary>老师端返回的提交结果。失败红字可重试；成功后锁定输入并自动关闭。</summary>
    public void SetResult(bool ok, string reason)
    {
        if (IsDisposed || _expired) return;
        _lblResult.Text = ok ? "✔ 签到成功！" : "✘ " + reason;
        _lblResult.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        if (!ok) return;

        _succeeded = true;
        _timer.Stop();
        _lblTime.Text = "签到完成";
        _txtName.Enabled = _txtId.Enabled = _btnSubmit.Enabled = false;
        var closeTimer = new System.Windows.Forms.Timer { Interval = 1800 };
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); closeTimer.Dispose(); Close(); };
        closeTimer.Start();
    }

    /// <summary>签到结束（倒计时归零或老师提前结束）。未成功的学生收到红字提示。</summary>
    public void Expire()
    {
        if (IsDisposed || _succeeded || _expired) return;
        _expired = true;
        _timer.Stop();
        _remaining = 0;
        _lblTime.Text = "已结束";
        _lblTime.ForeColor = Color.DimGray;
        _lblResult.Text = "签到已结束";
        _txtName.Enabled = _txtId.Enabled = _btnSubmit.Enabled = false;
        var closeTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); closeTimer.Dispose(); Close(); };
        closeTimer.Start();
    }

    private void TickSecond()
    {
        _remaining = Math.Max(0, _remaining - 1);
        UpdateTimeLabel();
        if (_remaining == 0) Expire();
    }

    private void UpdateTimeLabel()
    {
        _lblTime.Text = $"剩余 {_remaining / 60}:{_remaining % 60:D2}";
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FlashTray(); // 任务栏闪烁提醒，不抢占焦点
    }

    private void FlashTray()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hWnd = Handle,
            dwFlags = FLASHW_TRAY | FLASHW_COUNT,
            uCount = 3,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x2;
    private const uint FLASHW_COUNT = 0xC;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
