using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace SerialVmPowerController
{
    /// <summary>
    /// Windows Service that owns the COM port and runs the shutdown watchdog in the background.
    /// </summary>
    public class WatchdogService : ServiceBase
    {
        private const int VmStartSessionWaitSeconds = 180;
        private const int VmStartRetryDelaySeconds = 5;

        private readonly object _sync = new object();
        private SettingsStore _settingsStore;
        private StatusStore _statusStore;
        private LogService _log;
        private ShutdownCoordinator _shutdownCoordinator;
        private SerialMonitor _serialMonitor;
        private RuntimeStatus _runtimeStatus;
        private System.Timers.Timer _heartbeatTimer;
        private AppSettings _settings;

        /// <summary>
        /// Creates the Windows Service instance registered with Service Control Manager.
        /// </summary>
        public WatchdogService()
        {
            ServiceName = ServiceMetadata.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        /// <summary>
        /// Initializes logging, loads settings and starts serial monitoring.
        /// </summary>
        /// <param name="args">Service start arguments from Service Control Manager.</param>
        protected override void OnStart(string[] args)
        {
            _settingsStore = new SettingsStore();
            _statusStore = new StatusStore(_settingsStore.ConfigDirectory);
            _log = new LogService(_settingsStore.ConfigDirectory);
            _shutdownCoordinator = new ShutdownCoordinator(_log);
            _settings = _settingsStore.Load();

            _runtimeStatus = new RuntimeStatus
            {
                IsMonitoring = false,
                ComPortName = _settings.ComPortName,
                LastCts = false,
                ServiceMessage = "Service is starting."
            };

            _log.Info("Windows Service starting.");
            SaveStatus("Service is starting.");

            try
            {
                _serialMonitor = new SerialMonitor();
                _serialMonitor.CtsChanged += SerialMonitor_CtsChanged;
                _serialMonitor.Error += SerialMonitor_Error;
                _serialMonitor.Start(_settings.ComPortName, _settings.EnableDtr, _settings.EnableRts);

                lock (_sync)
                {
                    _runtimeStatus.IsMonitoring = true;
                    _runtimeStatus.LastCts = _serialMonitor.CurrentCts;
                }

                _log.Info("Service monitoring started on " + _settings.ComPortName + ".");
                SaveStatus("Monitoring " + _settings.ComPortName + ".");
            }
            catch (Exception ex)
            {
                _log.Error("Service could not start serial monitoring: " + ex.Message);
                SaveStatus("Serial monitoring failed: " + ex.Message);
            }

            StartHeartbeat();

            if (_settings.StartVmOnServiceStart)
            {
                Task.Run(new Action(StartVmInKvmMode));
            }
        }

        /// <summary>
        /// Stops serial monitoring and updates runtime status.
        /// </summary>
        protected override void OnStop()
        {
            _log?.Info("Windows Service stopping.");
            StopHeartbeat();
            StopSerialMonitor();
            SaveStatus("Service stopped.");
        }

        /// <summary>
        /// Logs host shutdown notifications from Service Control Manager.
        /// </summary>
        protected override void OnShutdown()
        {
            _log?.Info("Windows shutdown notification received by service.");
            StopHeartbeat();
            StopSerialMonitor();
            SaveStatus("Host shutdown notification received.");
            base.OnShutdown();
        }

        /// <summary>
        /// Handles CTS transitions and starts the shutdown sequence when CTS turns ON.
        /// </summary>
        /// <param name="ctsOn">True when CTS is active.</param>
        private void SerialMonitor_CtsChanged(bool ctsOn)
        {
            lock (_sync)
            {
                _runtimeStatus.LastCts = ctsOn;
                _runtimeStatus.LastCtsChangedAt = DateTime.Now;
            }

            _log.Info("Service detected CTS " + (ctsOn ? "ON" : "OFF") + ".");
            SaveStatus("CTS changed to " + (ctsOn ? "ON" : "OFF") + ".");

            if (ctsOn)
            {
                var snapshot = _settings.Clone();
                _shutdownCoordinator.HandleCtsAsync(snapshot, delegate
                {
                    return _serialMonitor != null && _serialMonitor.CurrentCts;
                });
            }
        }

        /// <summary>
        /// Starts the configured virtual machine in KVM mode inside the active user session.
        /// </summary>
        /// <remarks>
        /// vmware-kvm.exe is interactive and must not be launched directly in Session 0.
        /// The service retries for a short period because automatic logon may still be in progress.
        /// </remarks>
        private void StartVmInKvmMode()
        {
            try
            {
                Thread.Sleep(1000);

                if (!ValidateVmStartSettings())
                {
                    return;
                }

                if (_shutdownCoordinator.IsConfiguredVmRunning(_settings))
                {
                    _log.Info("VM is already running. KVM start skipped.");
                    SaveStatus("VM is already running.");
                    return;
                }

                var deadline = DateTime.Now.AddSeconds(VmStartSessionWaitSeconds);
                while (DateTime.Now <= deadline)
                {
                    var result = InteractiveProcessLauncher.Start(
                        _settings.VmwareKvmPath,
                        Quote(_settings.VmxPath),
                        Path.GetDirectoryName(_settings.VmwareKvmPath));

                    if (result.Succeeded)
                    {
                        _log.Info("VMware KVM start requested. " + result.Message);
                        SaveStatus("VMware KVM start requested.");
                        return;
                    }

                    _log.Warning("VMware KVM start attempt failed: " + result.Message);
                    SaveStatus("Waiting for interactive session to start VM.");
                    Thread.Sleep(VmStartRetryDelaySeconds * 1000);
                }

                _log.Error("VMware KVM start failed after waiting " + VmStartSessionWaitSeconds + " seconds.");
                SaveStatus("VMware KVM start failed.");
            }
            catch (Exception ex)
            {
                _log.Error("VMware KVM start failed: " + ex.Message);
                SaveStatus("VMware KVM start failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Verifies that the configured paths required for KVM startup exist.
        /// </summary>
        /// <returns>True when startup paths are valid.</returns>
        private bool ValidateVmStartSettings()
        {
            if (string.IsNullOrWhiteSpace(_settings.VmwareKvmPath) || !File.Exists(_settings.VmwareKvmPath))
            {
                _log.Error("vmware-kvm.exe was not found: " + _settings.VmwareKvmPath);
                SaveStatus("vmware-kvm.exe was not found.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_settings.VmxPath) || !File.Exists(_settings.VmxPath))
            {
                _log.Error("VMX file was not found: " + _settings.VmxPath);
                SaveStatus("VMX file was not found.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Quotes a single command-line argument.
        /// </summary>
        /// <param name="value">Argument value.</param>
        /// <returns>Quoted argument value.</returns>
        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Logs serial monitor errors and exposes them to the GUI status file.
        /// </summary>
        /// <param name="message">Serial monitor error message.</param>
        private void SerialMonitor_Error(string message)
        {
            _log.Error("Service serial monitor error: " + message);
            SaveStatus("Serial monitor error: " + message);
        }

        /// <summary>
        /// Starts a periodic heartbeat so the GUI can see that the service is alive.
        /// </summary>
        private void StartHeartbeat()
        {
            _heartbeatTimer = new System.Timers.Timer(5000);
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Elapsed += HeartbeatTimer_Elapsed;
            _heartbeatTimer.Start();
        }

        /// <summary>
        /// Stops the periodic runtime status heartbeat.
        /// </summary>
        private void StopHeartbeat()
        {
            if (_heartbeatTimer == null)
            {
                return;
            }

            _heartbeatTimer.Stop();
            _heartbeatTimer.Elapsed -= HeartbeatTimer_Elapsed;
            _heartbeatTimer.Dispose();
            _heartbeatTimer = null;
        }

        /// <summary>
        /// Periodically refreshes runtime-status.xml while the service is alive.
        /// </summary>
        /// <param name="sender">Timer that raised the event.</param>
        /// <param name="e">Timer event data.</param>
        private void HeartbeatTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            SaveStatus(null);
        }

        /// <summary>
        /// Stops and disposes the serial monitor if it was created.
        /// </summary>
        private void StopSerialMonitor()
        {
            if (_serialMonitor == null)
            {
                return;
            }

            try
            {
                _serialMonitor.Stop();
                _serialMonitor.CtsChanged -= SerialMonitor_CtsChanged;
                _serialMonitor.Error -= SerialMonitor_Error;
                _serialMonitor.Dispose();
            }
            finally
            {
                _serialMonitor = null;
                lock (_sync)
                {
                    if (_runtimeStatus != null)
                    {
                        _runtimeStatus.IsMonitoring = false;
                    }
                }
            }
        }

        /// <summary>
        /// Writes the current runtime status snapshot for the GUI.
        /// </summary>
        /// <param name="message">Optional latest service message.</param>
        private void SaveStatus(string message)
        {
            if (_statusStore == null || _runtimeStatus == null)
            {
                return;
            }

            RuntimeStatus snapshot;
            lock (_sync)
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    _runtimeStatus.ServiceMessage = message;
                }

                snapshot = new RuntimeStatus
                {
                    IsMonitoring = _runtimeStatus.IsMonitoring,
                    ComPortName = _runtimeStatus.ComPortName,
                    LastCts = _runtimeStatus.LastCts,
                    LastCtsChangedAt = _runtimeStatus.LastCtsChangedAt,
                    ServiceMessage = _runtimeStatus.ServiceMessage,
                    UpdatedAt = DateTime.Now
                };
            }

            _statusStore.Save(snapshot);
        }
    }
}

