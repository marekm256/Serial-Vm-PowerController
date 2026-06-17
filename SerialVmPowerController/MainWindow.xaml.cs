using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Threading;

namespace SerialVmPowerController
{
    /// <summary>
    /// Main WPF window that exposes watchdog settings, live CTS state and operational log.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly SettingsStore _settingsStore;
        private readonly StatusStore _statusStore;
        private readonly LogService _log;
        private readonly ShutdownCoordinator _shutdownCoordinator;
        private readonly ServiceManager _serviceManager;
        private readonly DispatcherTimer _serviceRefreshTimer;
        private readonly DispatcherTimer _logRefreshTimer;
        private AppSettings _settings;
        private long _lastLogFilePosition;

        /// <summary>
        /// Initializes services, loads persisted settings and optionally starts monitoring.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            _settingsStore = new SettingsStore();
            _settings = _settingsStore.Load();
            _statusStore = new StatusStore(_settingsStore.ConfigDirectory);
            _log = new LogService(_settingsStore.ConfigDirectory);
            _shutdownCoordinator = new ShutdownCoordinator(_log);
            _serviceManager = new ServiceManager();

            LoadRecentLogLines();
            RefreshPorts();
            ApplySettingsToUi();
            UpdateVmwareInstallStatus();
            UpdateCtsState(false);
            RefreshServiceStatus();

            _serviceRefreshTimer = new DispatcherTimer();
            _serviceRefreshTimer.Interval = TimeSpan.FromSeconds(2);
            _serviceRefreshTimer.Tick += ServiceRefreshTimer_Tick;
            _serviceRefreshTimer.Start();

            _logRefreshTimer = new DispatcherTimer();
            _logRefreshTimer.Interval = TimeSpan.FromSeconds(1);
            _logRefreshTimer.Tick += LogRefreshTimer_Tick;
            _logRefreshTimer.Start();

            _log.Info("Application started.");
        }

        /// <summary>
        /// Installs the current executable as the watchdog Windows Service.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void InstallServiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveSettingsFromUi(false))
            {
                return;
            }

            try
            {
                _serviceManager.Install(_settings.ServiceAutoStart);
                _log.Info("Windows Service installed.");
                RefreshServiceStatus();
            }
            catch (Exception ex)
            {
                ShowServiceError("Cannot install service", ex);
            }
        }

        /// <summary>
        /// Removes the watchdog Windows Service.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void UninstallServiceButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                this,
                "Uninstall the watchdog Windows Service?",
                "Confirm uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _serviceManager.Uninstall();
                _log.Info("Windows Service uninstalled.");
                RefreshServiceStatus();
            }
            catch (Exception ex)
            {
                ShowServiceError("Cannot uninstall service", ex);
            }
        }

        /// <summary>
        /// Starts the installed watchdog Windows Service.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void StartServiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _serviceManager.Start();
                _log.Info("Windows Service start requested.");
                RefreshServiceStatus();
            }
            catch (Exception ex)
            {
                ShowServiceError("Cannot start service", ex);
            }
        }

        /// <summary>
        /// Stops the installed watchdog Windows Service.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void StopServiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _serviceManager.Stop();
                _log.Info("Windows Service stop requested.");
                RefreshServiceStatus();
            }
            catch (Exception ex)
            {
                ShowServiceError("Cannot stop service", ex);
            }
        }

        /// <summary>
        /// Refreshes Service Control Manager and runtime heartbeat status.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void RefreshServiceButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshServiceStatus();
        }

        /// <summary>
        /// Persists the current UI settings next to the executable.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettingsFromUi(true);
        }

        /// <summary>
        /// Refreshes the COM port list shown in the editable combo box.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        /// <summary>
        /// Opens a file picker for the configured VMware .vmx file.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void BrowseVmxButton_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFile("VMware VMX files (*.vmx)|*.vmx|All files (*.*)|*.*", VmxPathBox.Text);
            if (!string.IsNullOrEmpty(path))
            {
                VmxPathBox.Text = path;
            }
        }

        /// <summary>
        /// Runs the manual soft VM stop action without freezing the UI.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private async void SoftStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveSettingsFromUi(false))
            {
                return;
            }

            SetActionButtonsEnabled(false);
            try
            {
                await _shutdownCoordinator.SoftStopVmAsync(_settings.Clone());
            }
            finally
            {
                SetActionButtonsEnabled(true);
            }
        }

        /// <summary>
        /// Runs the manual hard VM stop action without freezing the UI.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private async void HardStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveSettingsFromUi(false))
            {
                return;
            }

            SetActionButtonsEnabled(false);
            try
            {
                await _shutdownCoordinator.HardStopVmAsync(_settings.Clone());
            }
            finally
            {
                SetActionButtonsEnabled(true);
            }
        }

        /// <summary>
        /// Confirms and schedules a manual host shutdown.
        /// </summary>
        /// <param name="sender">Button that raised the event.</param>
        /// <param name="e">Click event data.</param>
        private void ShutdownHostButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                this,
                "Schedule host shutdown now?",
                "Confirm shutdown",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            if (!SaveSettingsFromUi(false))
            {
                return;
            }

            _shutdownCoordinator.ScheduleHostShutdown(_settings.HostShutdownDelaySeconds);
        }

        /// <summary>
        /// Releases the COM port when the WPF window is closing.
        /// </summary>
        /// <param name="sender">Window that raised the event.</param>
        /// <param name="e">Closing event data.</param>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _serviceRefreshTimer.Stop();
            _serviceRefreshTimer.Tick -= ServiceRefreshTimer_Tick;
            _logRefreshTimer.Stop();
            _logRefreshTimer.Tick -= LogRefreshTimer_Tick;
        }

        /// <summary>
        /// Refreshes the service status from the periodic UI timer.
        /// </summary>
        /// <param name="sender">Timer that raised the event.</param>
        /// <param name="e">Timer event data.</param>
        private void ServiceRefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshServiceStatus();
        }

        /// <summary>
        /// Reads new lines appended to shutdown.log by either the GUI or the service process.
        /// </summary>
        /// <param name="sender">Timer that raised the event.</param>
        /// <param name="e">Timer event data.</param>
        private void LogRefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshLogFromFile();
        }

        /// <summary>
        /// Reads SCM status and the service heartbeat file, then updates the header controls.
        /// </summary>
        private void RefreshServiceStatus()
        {
            try
            {
                var serviceStatus = _serviceManager.GetStatus();
                var runtimeStatus = _statusStore.Load();
                var heartbeatAge = DateTime.Now - runtimeStatus.UpdatedAt;
                var staleHeartbeat = serviceStatus.Status == ServiceControllerStatus.Running && heartbeatAge > TimeSpan.FromSeconds(20);

                ServiceStateText.Text = serviceStatus.StatusText;

                if (!serviceStatus.IsInstalled)
                {
                    MonitorStateText.Text = "Service not installed";
                    UpdateCtsState(false);
                }
                else if (staleHeartbeat)
                {
                    MonitorStateText.Text = "No recent heartbeat";
                    UpdateCtsState(runtimeStatus.LastCts);
                }
                else if (runtimeStatus.IsMonitoring)
                {
                    MonitorStateText.Text = "Monitoring " + runtimeStatus.ComPortName;
                    UpdateCtsState(runtimeStatus.LastCts);
                }
                else
                {
                    MonitorStateText.Text = runtimeStatus.ServiceMessage;
                    UpdateCtsState(runtimeStatus.LastCts);
                }

                UpdateServiceButtons(serviceStatus);
            }
            catch (Exception ex)
            {
                ServiceStateText.Text = "Unknown";
                MonitorStateText.Text = "Status read failed";
                UpdateServiceButtons(new ManagedServiceStatus { IsInstalled = false, StatusText = "Unknown" });
                _log.Error("Cannot refresh service status: " + ex.Message);
            }
        }

        /// <summary>
        /// Enables or disables service control buttons based on the current SCM state.
        /// </summary>
        /// <param name="serviceStatus">Latest service status from Service Control Manager.</param>
        private void UpdateServiceButtons(ManagedServiceStatus serviceStatus)
        {
            var installed = serviceStatus.IsInstalled;
            var running = installed && serviceStatus.Status == ServiceControllerStatus.Running;
            var stopped = installed && serviceStatus.Status == ServiceControllerStatus.Stopped;

            InstallServiceButton.IsEnabled = !installed;
            UninstallServiceButton.IsEnabled = installed;
            StartServiceButton.IsEnabled = installed && stopped;
            StopServiceButton.IsEnabled = installed && running;
        }

        /// <summary>
        /// Validates UI fields, stores them in settings.xml and updates the in-memory settings object.
        /// </summary>
        /// <param name="showSavedMessage">Whether to write a "Settings saved" line to the log.</param>
        /// <returns>True when settings were valid and saved.</returns>
        private bool SaveSettingsFromUi(bool showSavedMessage)
        {
            try
            {
                var updated = new AppSettings
                {
                    ComPortName = GetComboText(ComPortComboBox.Text, _settings.ComPortName),
                    VmrunPath = AppSettings.DefaultVmrunPath,
                    VmwareKvmPath = AppSettings.DefaultVmwareKvmPath,
                    VmxPath = VmxPathBox.Text.Trim(),
                    DebounceSeconds = ParseInt(DebounceSecondsBox.Text, "CTS debounce", 0, 3600),
                    SoftTimeoutSeconds = ParseInt(SoftTimeoutSecondsBox.Text, "Soft timeout", 1, 7200),
                    HardTimeoutSeconds = ParseInt(HardTimeoutSecondsBox.Text, "Hard timeout", 1, 7200),
                    HostShutdownDelaySeconds = ParseInt(HostShutdownDelaySecondsBox.Text, "Host shutdown delay", 0, 3600),
                    EnableDtr = DtrCheckBox.IsChecked == true,
                    EnableRts = RtsCheckBox.IsChecked == true,
                    BlockCtsShutdown = BlockCtsShutdownCheckBox.IsChecked == true,
                    TestMode = BlockCtsShutdownCheckBox.IsChecked == true,
                    MaintenanceDisabled = BlockCtsShutdownCheckBox.IsChecked == true,
                    ShutdownHostAfterVm = ShutdownHostCheckBox.IsChecked == true,
                    StartMonitoringOnLaunch = false,
                    ServiceAutoStart = ServiceAutoStartCheckBox.IsChecked == true,
                    StartVmOnServiceStart = StartVmOnServiceStartCheckBox.IsChecked == true
                };

                _settingsStore.Save(updated);
                _settings = updated;
                UpdateVmwareInstallStatus();

                if (showSavedMessage)
                {
                    _log.Info("Settings saved.");
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Settings were not saved: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>
        /// Copies loaded settings into WPF controls.
        /// </summary>
        private void ApplySettingsToUi()
        {
            ComPortComboBox.Text = _settings.ComPortName;
            VmxPathBox.Text = _settings.VmxPath;
            DebounceSecondsBox.Text = _settings.DebounceSeconds.ToString();
            SoftTimeoutSecondsBox.Text = _settings.SoftTimeoutSeconds.ToString();
            HardTimeoutSecondsBox.Text = _settings.HardTimeoutSeconds.ToString();
            HostShutdownDelaySecondsBox.Text = _settings.HostShutdownDelaySeconds.ToString();
            DtrCheckBox.IsChecked = _settings.EnableDtr;
            RtsCheckBox.IsChecked = _settings.EnableRts;
            BlockCtsShutdownCheckBox.IsChecked = _settings.BlockCtsShutdown;
            ShutdownHostCheckBox.IsChecked = _settings.ShutdownHostAfterVm;
            ServiceAutoStartCheckBox.IsChecked = _settings.ServiceAutoStart;
            StartVmOnServiceStartCheckBox.IsChecked = _settings.StartVmOnServiceStart;
        }

        /// <summary>
        /// Shows whether VMware Workstation command-line tools exist in the expected default path.
        /// </summary>
        private void UpdateVmwareInstallStatus()
        {
            var vmrunOk = File.Exists(AppSettings.DefaultVmrunPath);
            var kvmOk = File.Exists(AppSettings.DefaultVmwareKvmPath);

            if (vmrunOk && kvmOk)
            {
                VmwareInstallStatusText.Text = "OK - VMware Workstation tools found in the default path.";
                VmwareInstallStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                return;
            }

            VmwareInstallStatusText.Text =
                "Missing VMware tool in default path. Expected vmrun.exe and vmware-kvm.exe in C:\\Program Files (x86)\\VMware\\VMware Workstation.";
            VmwareInstallStatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
        }

        /// <summary>
        /// Loads the available Windows COM ports while keeping the currently configured value selectable.
        /// </summary>
        private void RefreshPorts()
        {
            var selected = ComPortComboBox.Text;
            var ports = SerialPort.GetPortNames().OrderBy(x => x).ToList();

            if (!string.IsNullOrWhiteSpace(_settings.ComPortName) && !ports.Contains(_settings.ComPortName, StringComparer.OrdinalIgnoreCase))
            {
                ports.Add(_settings.ComPortName);
            }

            if (!string.IsNullOrWhiteSpace(selected) && !ports.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                ports.Add(selected);
            }

            ComPortComboBox.ItemsSource = ports.OrderBy(x => x).ToList();
            ComPortComboBox.Text = string.IsNullOrWhiteSpace(selected) ? _settings.ComPortName : selected;
        }

        /// <summary>
        /// Updates the visible CTS status indicator.
        /// </summary>
        /// <param name="ctsOn">True when CTS is ON.</param>
        private void UpdateCtsState(bool ctsOn)
        {
            CtsStateText.Text = ctsOn ? "ON" : "OFF";
            CtsStateText.Foreground = ctsOn
                ? System.Windows.Media.Brushes.DarkRed
                : System.Windows.Media.Brushes.DarkGreen;
        }

        /// <summary>
        /// Loads the recent log tail into the log text box during startup.
        /// </summary>
        private void LoadRecentLogLines()
        {
            foreach (var line in _log.ReadTailLines(120))
            {
                AppendLogLine(line);
            }

            _lastLogFilePosition = _log.GetLength();
        }

        /// <summary>
        /// Appends any new text written to shutdown.log since the previous read.
        /// </summary>
        private void RefreshLogFromFile()
        {
            long newPosition;
            var newText = _log.ReadFrom(_lastLogFilePosition, out newPosition);
            _lastLogFilePosition = newPosition;

            if (!string.IsNullOrEmpty(newText))
            {
                AppendLogText(newText);
            }
        }

        /// <summary>
        /// Appends a log line to the UI from any thread.
        /// </summary>
        /// <param name="line">Already formatted log line.</param>
        private void AppendLogLine(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(AppendLogLine), line);
                return;
            }

            LogTextBox.AppendText(line + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        }

        /// <summary>
        /// Appends raw text from the shared log file to the UI from any thread.
        /// </summary>
        /// <param name="text">Raw text read from shutdown.log.</param>
        private void AppendLogText(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(AppendLogText), text);
                return;
            }

            LogTextBox.AppendText(text);
            LogTextBox.ScrollToEnd();
        }

        /// <summary>
        /// Enables or disables manual action buttons while a manual VMware command is running.
        /// </summary>
        /// <param name="enabled">True to enable the buttons.</param>
        private void SetActionButtonsEnabled(bool enabled)
        {
            SoftStopButton.IsEnabled = enabled;
            HardStopButton.IsEnabled = enabled;
            ShutdownHostButton.IsEnabled = enabled;
        }

        /// <summary>
        /// Shows a service management error and records it in the application log.
        /// </summary>
        /// <param name="title">Dialog title.</param>
        /// <param name="ex">Exception to display.</param>
        private void ShowServiceError(string title, Exception ex)
        {
            _log.Error(title + ": " + ex.Message);
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// Opens a standard Windows file picker and returns the selected path.
        /// </summary>
        /// <param name="filter">OpenFileDialog filter string.</param>
        /// <param name="currentPath">Existing path used to choose the initial directory.</param>
        /// <returns>Selected file path, or null when the dialog is cancelled.</returns>
        private static string BrowseFile(string filter, string currentPath)
        {
            var dialog = new OpenFileDialog();
            dialog.Filter = filter;

            try
            {
                if (!string.IsNullOrWhiteSpace(currentPath))
                {
                    var directory = Path.GetDirectoryName(currentPath);
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    {
                        dialog.InitialDirectory = directory;
                    }
                }
            }
            catch
            {
                // Ignore invalid current path.
            }

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        /// <summary>
        /// Returns combo-box text or a fallback value when the text is empty.
        /// </summary>
        /// <param name="value">Current combo-box text.</param>
        /// <param name="fallback">Fallback value.</param>
        /// <returns>Trimmed combo-box text or fallback.</returns>
        private static string GetComboText(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        /// <summary>
        /// Parses and range-checks a numeric UI setting.
        /// </summary>
        /// <param name="value">Text to parse.</param>
        /// <param name="name">Human-readable field name used in validation errors.</param>
        /// <param name="min">Minimum accepted value.</param>
        /// <param name="max">Maximum accepted value.</param>
        /// <returns>Parsed integer value.</returns>
        private static int ParseInt(string value, string name, int min, int max)
        {
            int parsed;
            if (!int.TryParse(value, out parsed))
            {
                throw new InvalidOperationException(name + " must be a number.");
            }

            if (parsed < min || parsed > max)
            {
                throw new InvalidOperationException(name + " must be between " + min + " and " + max + ".");
            }

            return parsed;
        }
    }
}


