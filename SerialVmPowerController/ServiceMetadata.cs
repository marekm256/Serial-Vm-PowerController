namespace SerialVmPowerController
{
    /// <summary>
    /// Central definition of Windows Service identity values.
    /// </summary>
    public static class ServiceMetadata
    {
        /// <summary>
        /// Internal service name used by Service Control Manager commands.
        /// </summary>
        public const string ServiceName = "SerialVmPowerController";

        /// <summary>
        /// Human-readable service name shown in Services.msc.
        /// </summary>
        public const string DisplayName = "Serial VM Power Controller";

        /// <summary>
        /// Service description shown in Windows service management tools.
        /// </summary>
        public const string Description = "Starts a VMware VM, watches a serial CTS signal, and controls VM/host shutdown.";
    }
}

