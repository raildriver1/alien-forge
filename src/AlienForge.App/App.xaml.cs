using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace AlienForge.App;

public partial class App : Application
{
    /// <summary>Where unhandled exceptions are recorded, next to the executable.</summary>
    public static string CrashLogPath { get; } = Path.Combine(
        AppContext.BaseDirectory, "AlienForge.errors.log");

    private bool _reported;

    protected override void OnStartup(StartupEventArgs e)
    {
        // A parsing bug in one asset should not take the whole browser down.
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    /// <remarks>
    /// Deliberately does NOT open a dialog straight away. MessageBox.Show pumps the
    /// dispatcher, so if the exception came from a layout pass the layout runs again
    /// inside the dialog, throws again, re-enters this handler, and the stack
    /// overflows: the crash then reports itself as "cannot create another guard page
    /// for the stack" and hides the real fault entirely. So everything is written to
    /// a log first, and at most one dialog is queued for after the current pass.
    /// </remarks>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Append(e.Exception);

        if (_reported)
            return;
        _reported = true;

        string message = $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
                         $"Подробности записаны в:\n{CrashLogPath}";

        // Queued at background priority so the failing layout pass unwinds first.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            MessageBox.Show(message, "AlienForge", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }));
    }

    private static void Append(Exception exception)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                sb.AppendLine($"{current.GetType().FullName}: {current.Message}");
                sb.AppendLine(current.StackTrace);
                sb.AppendLine();
            }
            File.AppendAllText(CrashLogPath, sb.ToString());
        }
        catch (Exception)
        {
            // Nothing useful can be done if even logging fails.
        }
    }
}
