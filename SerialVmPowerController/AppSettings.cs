using System;

namespace SerialVmPowerController
{
    /// <summary>
    /// Serializable application configuration stored next to the executable.
    /// </summary>
    /// <remarks>
    /// Defaults are tuned for a Siemens panel setup with VMware Workstation.
    /// The VMX file is intentionally left empty so each deployment selects its own VM.
    /// </remarks>
    [Serializable]
    public class AppSettings
    {
        /// <summary>
        /// Default VMware Workstation vmrun.exe path expected by this deployment.
        /// </summary>
        public const string DefaultVmrunPath = @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe";

        /// <summary>
        /// Default VMware Workstation vmware-kvm.exe path expected by this deployment.
        /// </summary>
        public const string DefaultVmwareKvmPath = @"C:\Program Files (x86)\VMware\VMware Workstation\vmware-kvm.exe";

        /// <summary>
        /// Creates settings with safe default values.
        /// </summary>
        /// <remarks>
        /// CTS shutdown blocking is enabled by default so the first run only logs CTS events.
        /// </remarks>
        public AppSettings()
        {
            ComPortName = "COM3";
            VmrunPath = DefaultVmrunPath;
            VmwareKvmPath = DefaultVmwareKvmPath;
            VmxPath = string.Empty;
            DebounceSeconds = 3;
            SoftTimeoutSeconds = 60;
            HardTimeoutSeconds = 30;
            HostShutdownDelaySeconds = 5;
            EnableDtr = true;
            EnableRts = true;
            BlockCtsShutdown = true;
            TestMode = true;
            MaintenanceDisabled = true;
            ShutdownHostAfterVm = true;
            StartMonitoringOnLaunch = true;
            ServiceAutoStart = true;
            StartVmOnServiceStart = true;
        }

        /// <summary>
        /// Serial port watched for the CTS signal, for example COM3.
        /// </summary>
        public string ComPortName { get; set; }

        /// <summary>
        /// Full path to VMware vmrun.exe used to control the virtual machine.
        /// </summary>
        public string VmrunPath { get; set; }

        /// <summary>
        /// Full path to VMware vmware-kvm.exe used to show the VM in KVM mode.
        /// </summary>
        public string VmwareKvmPath { get; set; }

        /// <summary>
        /// Full path to the VMware .vmx file that identifies the XP virtual machine.
        /// </summary>
        public string VmxPath { get; set; }

        /// <summary>
        /// Number of seconds CTS must remain active before shutdown begins.
        /// </summary>
        public int DebounceSeconds { get; set; }

        /// <summary>
        /// Maximum number of seconds to wait for the VMware soft stop command.
        /// </summary>
        /// <remarks>
        /// The soft command can intentionally time out when Windows XP reaches
        /// the "safe to turn off" screen while the VMware process remains running.
        /// </remarks>
        public int SoftTimeoutSeconds { get; set; }

        /// <summary>
        /// Maximum number of seconds to wait for the VMware hard stop command.
        /// </summary>
        public int HardTimeoutSeconds { get; set; }

        /// <summary>
        /// Delay passed to shutdown.exe before the Windows 11 host powers off.
        /// </summary>
        public int HostShutdownDelaySeconds { get; set; }

        /// <summary>
        /// Enables the serial DTR output line while the COM port is open.
        /// </summary>
        /// <remarks>
        /// This is useful for tests where pin 4 (DTR) is bridged to pin 8 (CTS).
        /// </remarks>
        public bool EnableDtr { get; set; }

        /// <summary>
        /// Enables the serial RTS output line while the COM port is open.
        /// </summary>
        public bool EnableRts { get; set; }

        /// <summary>
        /// Blocks automatic VM/host shutdown after CTS and logs the event only.
        /// </summary>
        public bool BlockCtsShutdown { get; set; }

        /// <summary>
        /// Legacy setting kept so older settings.xml files can still be loaded.
        /// </summary>
        public bool TestMode { get; set; }

        /// <summary>
        /// Legacy setting kept so older settings.xml files can still be loaded.
        /// </summary>
        public bool MaintenanceDisabled { get; set; }

        /// <summary>
        /// Controls whether the host shuts down after the VM stop sequence.
        /// </summary>
        public bool ShutdownHostAfterVm { get; set; }

        /// <summary>
        /// Starts serial monitoring automatically when the application launches.
        /// </summary>
        /// <remarks>
        /// Kept for backward compatibility with older settings files. In service
        /// mode the Windows Service always monitors when it is started.
        /// </remarks>
        public bool StartMonitoringOnLaunch { get; set; }

        /// <summary>
        /// Installs the Windows Service with automatic startup when selected in the GUI.
        /// </summary>
        public bool ServiceAutoStart { get; set; }

        /// <summary>
        /// Starts the configured virtual machine in KVM mode when the Windows Service starts.
        /// </summary>
        public bool StartVmOnServiceStart { get; set; }

        /// <summary>
        /// Forces VMware tool paths back to the expected default installation paths.
        /// </summary>
        public void NormalizeFixedPaths()
        {
            VmrunPath = DefaultVmrunPath;
            VmwareKvmPath = DefaultVmwareKvmPath;

            if (TestMode || MaintenanceDisabled)
            {
                BlockCtsShutdown = true;
            }

            TestMode = BlockCtsShutdown;
            MaintenanceDisabled = BlockCtsShutdown;
        }

        /// <summary>
        /// Creates a detached copy so background shutdown work is not affected by later UI edits.
        /// </summary>
        /// <returns>A new <see cref="AppSettings"/> instance with the same values.</returns>
        public AppSettings Clone()
        {
            return new AppSettings
            {
                ComPortName = ComPortName,
                VmrunPath = VmrunPath,
                VmwareKvmPath = VmwareKvmPath,
                VmxPath = VmxPath,
                DebounceSeconds = DebounceSeconds,
                SoftTimeoutSeconds = SoftTimeoutSeconds,
                HardTimeoutSeconds = HardTimeoutSeconds,
                HostShutdownDelaySeconds = HostShutdownDelaySeconds,
                EnableDtr = EnableDtr,
                EnableRts = EnableRts,
                BlockCtsShutdown = BlockCtsShutdown,
                TestMode = TestMode,
                MaintenanceDisabled = MaintenanceDisabled,
                ShutdownHostAfterVm = ShutdownHostAfterVm,
                StartMonitoringOnLaunch = StartMonitoringOnLaunch,
                ServiceAutoStart = ServiceAutoStart,
                StartVmOnServiceStart = StartVmOnServiceStart
            };
        }
    }
}

