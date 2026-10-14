using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

namespace Studio.UI;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\StudioV01.SingleInstance";
    private Mutex? _instanceMutex;
    private static readonly string StartupLogPath =
        Path.Combine(AppContext.BaseDirectory, "startup.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        _instanceMutex = new Mutex(true, InstanceMutexName, out createdNew);
        if (!createdNew)
        {
            WriteStartup("Inicio bloqueado: ya existe una instancia de STUDIO V01.");
            MessageBox.Show(
                "STUDIO V01 ya está abierto. Cerrá la otra ventana antes de iniciar otra instancia.",
                "STUDIO V01",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        WriteStartup("=== STUDIO V01 START ===");
        WriteStartup("Executable: " + Environment.ProcessPath);
        WriteStartup("BaseDirectory: " + AppContext.BaseDirectory);
        WriteStartup("OS: " + Environment.OSVersion);
        WriteStartup(".NET: " + Environment.Version);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteException("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                "STUDIO V01 no pudo iniciar correctamente.\n\n" +
                args.Exception.Message +
                "\n\nRevisá startup.log.",
                "STUDIO V01 — ERROR",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteException("AppDomain.UnhandledException", ex);
            else
                WriteStartup("AppDomain.UnhandledException: " + args.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteException("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        try
        {
            base.OnStartup(e);
            WriteStartup("Creando MainWindow...");
            var window = new MainWindow();
            MainWindow = window;
            WriteStartup("MainWindow creada. Mostrando ventana...");
            window.Show();
            window.Activate();
            window.Focus();
            WriteStartup("VENTANA MOSTRADA.");
        }
        catch (Exception ex)
        {
            WriteException("ERROR FATAL EN ARRANQUE", ex);
            MessageBox.Show(
                "STUDIO V01 no pudo abrir.\n\n" +
                ex +
                "\n\nSe creó startup.log junto al EXE.",
                "STUDIO V01 — ERROR FATAL",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // El mutex puede no pertenecer al hilo de salida en un cierre excepcional.
        }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }

    private static void WriteStartup(string message)
    {
        try
        {
            File.AppendAllText(
                StartupLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // El diagnóstico nunca debe impedir el arranque.
        }
    }

    private static void WriteException(string title, Exception ex)
    {
        WriteStartup(title);
        WriteStartup(ex.ToString());
    }
}
