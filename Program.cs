namespace ImbotechConsulting.PiExplorer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Updater.Cleanup();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
