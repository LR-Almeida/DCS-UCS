namespace UCS.Client;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "DCS-UCS-Client-SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("UCS-Client is already running.", "DCS-UCS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
