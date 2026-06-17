using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CheckupAddIn.DesignHarness
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandled;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        }

        private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception);
            MessageBox.Show(e.Exception.ToString(), "Design Harness — unhandled error");
            e.Handled = true;   // keep the harness alive so the designer can continue
        }

        private static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "CheckupHarness_error.log"),
                    $"{DateTime.Now:s}\n{ex}\n\n");
            }
            catch { }
        }
    }
}
