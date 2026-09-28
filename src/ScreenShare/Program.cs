namespace ScreenShare;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("发生未预期的错误：" + e.Exception.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new UI.MainForm());
    }
}
