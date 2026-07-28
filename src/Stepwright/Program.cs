using System.Text;
using Stepwright.Config;
using Stepwright.Ui;

namespace Stepwright;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppSettings settings = AppSettings.Load();
        Theme.Use(settings.DarkTheme);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => Report(e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            Application.Run(new MainForm(settings));
        }
        finally
        {
            settings.Save();
        }
    }

    private static void Report(Exception? error)
    {
        if (error is null)
        {
            return;
        }

        try
        {
            string folder = AppSettings.SettingsFolder;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "errors.log");

            var entry = new StringBuilder();
            entry.AppendLine(DateTime.Now.ToString("u"));
            entry.AppendLine(error.ToString());
            entry.AppendLine();
            File.AppendAllText(path, entry.ToString());

            MessageBox.Show(
                "Something went wrong: " + error.Message + Environment.NewLine + Environment.NewLine
                + "The details were written to " + path,
                "Stepwright",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // If even the error report fails there is nothing left to try.
        }
    }
}
