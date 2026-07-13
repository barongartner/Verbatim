using System.Text;

namespace Verbatim;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // second launches focus the running instance instead of racing it
        using var mutex = new Mutex(true, @"Local\VerbatimSingleInstance", out bool isFirst);
        if (!isFirst) return;

        // Turn silent UI-thread crashes into a message + a log file the user can
        // send, instead of the app just vanishing ("it doesn't work, no idea why").
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            ReportFatal(ex);
        }
        finally
        {
            NAudio.MediaFoundation.MediaFoundationApi.Shutdown();
        }
    }

    private static void ReportFatal(Exception? ex)
    {
        if (ex is null) return;
        string logPath;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Verbatim");
            Directory.CreateDirectory(dir);
            logPath = Path.Combine(dir, "error.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { logPath = "(could not write log)"; }

        try
        {
            MessageBox.Show(
                $"Verbatim hit an unexpected error and needs to close.\n\n{ex.Message}\n\nDetails were saved to:\n{logPath}",
                "Verbatim", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* no UI available */ }
    }
}
