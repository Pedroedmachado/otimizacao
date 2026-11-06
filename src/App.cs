using System;
using System.Threading;
using System.Windows;

[assembly: System.Reflection.AssemblyTitle("VoltCore Optimizer")]
[assembly: System.Reflection.AssemblyProduct("VoltCore Optimizer")]
[assembly: System.Reflection.AssemblyDescription("Otimizador para PC gamer: debloat, desempenho e redução de memória")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace VoltCore
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--maintenance")
            {
                try { Maintenance.Run(); } catch (Exception ex) { Store.Log("ERRO manutenção: " + ex.Message); }
                return 0;
            }

            bool first;
            using (var mutex = new Mutex(true, "VoltCoreOptimizer_SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show("O VoltCore já está aberto (veja o ícone perto do relógio).", "VoltCore", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Store.Log("ERRO " + e.Exception);
                    MessageBox.Show("Ocorreu um erro:\n\n" + e.Exception.Message + "\n\nDetalhes salvos em " + Store.PathOf("log.txt"), "VoltCore", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                var w = new MainWindow();
                if (args.Length > 1 && args[0] == "--screenshots")
                {
                    var dir = args[1];
                    w.Loaded += async (s, e) => await w.CaptureScreens(dir);
                }
                w.Show();
                app.Run();
            }
            return 0;
        }
    }
}
