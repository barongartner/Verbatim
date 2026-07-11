namespace Verbatim;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // second launches focus the running instance instead of racing it
        using var mutex = new Mutex(true, @"Local\VerbatimSingleInstance", out bool isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        try
        {
            Application.Run(new MainForm());
        }
        finally
        {
            NAudio.MediaFoundation.MediaFoundationApi.Shutdown();
        }
    }
}
