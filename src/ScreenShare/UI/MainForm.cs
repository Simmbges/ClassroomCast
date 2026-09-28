namespace ScreenShare.UI;

/// <summary>主窗体：两个页签分别是老师端（Server）与学生端（Client）。</summary>
public sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "教室屏幕共享（局域网屏幕直播）";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(510, 520);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var serverTab = new TabPage("Server 老师端");
        var clientTab = new TabPage("Client 学生端");
        serverTab.Controls.Add(new ServerPanel { Dock = DockStyle.Fill });
        clientTab.Controls.Add(new ClientPanel { Dock = DockStyle.Fill });
        tabs.TabPages.Add(serverTab);
        tabs.TabPages.Add(clientTab);
        Controls.Add(tabs);
    }
}
