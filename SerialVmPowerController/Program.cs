using System;
using System.Linq;
using System.ServiceProcess;
using System.Windows;

namespace SerialVmPowerController
{
    /// <summary>
    /// Single executable entry point for both GUI and Windows Service modes.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Starts the process either as a Windows Service or as the WPF control panel.
        /// </summary>
        /// <param name="args">Command-line arguments. Use --service for service mode.</param>
        [STAThread]
        public static void Main(string[] args)
        {
            if (ShouldRunAsService(args))
            {
                ServiceBase.Run(new WatchdogService());
                return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        /// <summary>
        /// Detects whether the executable was launched by Service Control Manager.
        /// </summary>
        /// <param name="args">Command-line arguments passed to the process.</param>
        /// <returns>True when service mode should be used.</returns>
        private static bool ShouldRunAsService(string[] args)
        {
            return args.Any(x => string.Equals(x, "--service", StringComparison.OrdinalIgnoreCase))
                || !Environment.UserInteractive;
        }
    }
}

