namespace ScreenShare.UI;

/// <summary>
/// .NET 8 WinForms 在高 DPI 进程中创建控件时不会自动做 96→DPI 的初始缩放
/// （AutoScaleMode.Font/Dpi 在此场景均不生效，已实测验证），
/// 因此布局数值统一按 96 DPI 基线书写，运行时用本类按 DeviceDpi/96 手动缩放。
/// </summary>
internal static class DpiScale
{
    /// <summary>进程初始 DPI 相对 96 基线的缩放因子。</summary>
    public static float Factor(Control c) => c.DeviceDpi / 96f;

    /// <summary>递归缩放 root 的全部子控件（不缩放 root 自身，由调用方处理）。
    /// 遇到 UserControl 停止下钻——它会在自己的构造函数中完成内部缩放。</summary>
    public static void ScaleChildren(Control root, float factor)
    {
        if (Math.Abs(factor - 1f) < 0.02f) return; // 96 DPI 环境无需缩放
        foreach (Control child in root.Controls)
        {
            ScaleOne(child, factor);
            if (child is UserControl) continue;
            ScaleChildren(child, factor);
        }
    }

    private static void ScaleOne(Control c, float factor)
    {
        c.Left = (int)Math.Round(c.Left * factor);
        c.Top = (int)Math.Round(c.Top * factor);

        if (c.AutoSize) return; // 尺寸随字体自动（按钮/自动标签）

        // 单行输入类高度由字体决定，只缩宽度
        int h = c is TextBox or ComboBox or NumericUpDown ? c.Height : (int)Math.Round(c.Height * factor);
        c.Size = new Size((int)Math.Round(c.Width * factor), h);

        if (c is ListView lv)
            foreach (ColumnHeader col in lv.Columns)
                col.Width = (int)Math.Round(col.Width * factor);
    }
}
