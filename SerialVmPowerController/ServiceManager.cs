using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace SerialVmPowerController
{
    /// <summary>
    /// Installs, removes and controls the watchdog Windows Service from the GUI.
    /// </summary>
    public class ServiceManager
    {
        private readonly string _exePath;

        /// <summary>
        /// Creates a service manager for the current executable.
        /// </summary>
        public ServiceManager()
        {
            _exePath = Process.GetCurrentProcess().MainModule.FileName;
        }

        /// <summary>
        /// Returns the current SCM status for the watchdog service.
        /// </summary>
        /// <returns>Status information for the service.</returns>
        public ManagedServiceStatus GetStatus()
        {
            var controller = FindService();
            if (controller == null)
            {
                return new ManagedServiceStatus
                {
                    IsInstalled = false,
                    StatusText = "Not installed"
                };
            }

            using (controller)
            {
                return new ManagedServiceStatus
                {
                    IsInstalled = true,
                    Status = controller.Status,
                    StatusText = controller.Status.ToString()
                };
            }
        }

        /// <summary>
        /// Installs the current executable as a Windows Service.
        /// </summary>
        /// <param name="automaticStartup">True to set service startup type to automatic.</param>
        public void Install(bool automaticStartup)
        {
            if (GetStatus().IsInstalled)
            {
                throw new InvalidOperationException("Service is already installed.");
            }

            if (!File.Exists(_exePath))
            {
                throw new FileNotFoundException("Current executable was not found.", _exePath);
            }

            var serviceCommand = Quote(_exePath) + " --service";
            var startup = automaticStartup ? "auto" : "demand";

            RunSc("create " + ServiceMetadata.ServiceName
                + " binPath= " + Quote(serviceCommand)
                + " start= " + startup
                + " DisplayName= " + Quote(ServiceMetadata.DisplayName));

            RunSc("description " + ServiceMetadata.ServiceName + " " + Quote(ServiceMetadata.Description));
        }

        /// <summary>
        /// Removes the watchdog Windows Service from Service Control Manager.
        /// </summary>
        public void Uninstall()
        {
            var status = GetStatus();
            if (!status.IsInstalled)
            {
                throw new InvalidOperationException("Service is not installed.");
            }

            if (status.Status != ServiceControllerStatus.Stopped)
            {
                Stop();
            }

            RunSc("delete " + ServiceMetadata.ServiceName);
        }

        /// <summary>
        /// Starts the installed watchdog service and waits until it is running.
        /// </summary>
        public void Start()
        {
            using (var controller = RequireService())
            {
                if (controller.Status == ServiceControllerStatus.Running)
                {
                    return;
                }

                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }

        /// <summary>
        /// Stops the installed watchdog service and waits until it is stopped.
        /// </summary>
        public void Stop()
        {
            using (var controller = RequireService())
            {
                if (controller.Status == ServiceControllerStatus.Stopped)
                {
                    return;
                }

                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }

        /// <summary>
        /// Finds the watchdog service without throwing when it is not installed.
        /// </summary>
        /// <returns>ServiceController instance or null.</returns>
        private static ServiceController FindService()
        {
            return ServiceController.GetServices()
                .FirstOrDefault(x => string.Equals(x.ServiceName, ServiceMetadata.ServiceName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets the watchdog service or throws a clear error when it is not installed.
        /// </summary>
        /// <returns>ServiceController for the watchdog service.</returns>
        private static ServiceController RequireService()
        {
            var controller = FindService();
            if (controller == null)
            {
                throw new InvalidOperationException("Service is not installed.");
            }

            return controller;
        }

        /// <summary>
        /// Runs sc.exe with captured output and throws when Service Control Manager reports an error.
        /// </summary>
        /// <param name="arguments">Arguments passed to sc.exe.</param>
        private static void RunSc(string arguments)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();

            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        output.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        error.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(30000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Best effort cleanup after timeout.
                    }

                    throw new System.TimeoutException("sc.exe timed out.");
                }

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "sc.exe failed with exit code " + process.ExitCode + ". "
                        + output.ToString().Trim() + " " + error.ToString().Trim());
                }
            }

            Thread.Sleep(500);
        }

        /// <summary>
        /// Quotes an argument for the simple sc.exe command lines used here.
        /// </summary>
        /// <param name="value">Argument to quote.</param>
        /// <returns>Quoted argument.</returns>
        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }

    /// <summary>
    /// Simple DTO with Service Control Manager status values for the GUI.
    /// </summary>
    public class ManagedServiceStatus
    {
        /// <summary>
        /// True when the service exists in Service Control Manager.
        /// </summary>
        public bool IsInstalled { get; set; }

        /// <summary>
        /// Native ServiceController status when installed.
        /// </summary>
        public ServiceControllerStatus Status { get; set; }

        /// <summary>
        /// Human-readable service status.
        /// </summary>
        public string StatusText { get; set; }
    }
}

