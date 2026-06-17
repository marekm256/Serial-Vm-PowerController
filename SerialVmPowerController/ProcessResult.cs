namespace SerialVmPowerController
{
    /// <summary>
    /// Captures the outcome of an external process execution.
    /// </summary>
    public class ProcessResult
    {
        /// <summary>
        /// Process exit code when the process ended normally; null when it timed out or could not be read.
        /// </summary>
        public int? ExitCode { get; set; }

        /// <summary>
        /// True when the process exceeded the configured timeout.
        /// </summary>
        public bool TimedOut { get; set; }

        /// <summary>
        /// Captured standard output text.
        /// </summary>
        public string Output { get; set; }

        /// <summary>
        /// Captured standard error text.
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// True when the process finished before timeout with exit code 0.
        /// </summary>
        public bool Succeeded
        {
            get { return !TimedOut && ExitCode == 0; }
        }
    }
}

