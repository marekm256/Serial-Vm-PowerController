using System;

namespace SerialVmPowerController
{
    /// <summary>
    /// Runtime status snapshot written by the Windows Service and read by the GUI.
    /// </summary>
    [Serializable]
    public class RuntimeStatus
    {
        /// <summary>
        /// Creates a status snapshot with default timestamps.
        /// </summary>
        public RuntimeStatus()
        {
            UpdatedAt = DateTime.Now;
            LastCtsChangedAt = DateTime.MinValue;
            ServiceMessage = "No service status has been written yet.";
        }

        /// <summary>
        /// True when the service has the serial port open.
        /// </summary>
        public bool IsMonitoring { get; set; }

        /// <summary>
        /// COM port currently used by the service.
        /// </summary>
        public string ComPortName { get; set; }

        /// <summary>
        /// Last CTS state observed by the service.
        /// </summary>
        public bool LastCts { get; set; }

        /// <summary>
        /// Timestamp of the last CTS transition, or DateTime.MinValue when none was observed.
        /// </summary>
        public DateTime LastCtsChangedAt { get; set; }

        /// <summary>
        /// Last operational service message.
        /// </summary>
        public string ServiceMessage { get; set; }

        /// <summary>
        /// Last time the service refreshed this status file.
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }
}

