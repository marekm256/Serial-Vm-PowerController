using System;
using System.IO;
using System.Xml.Serialization;

namespace SerialVmPowerController
{
    /// <summary>
    /// Persists a lightweight service status XML file for the GUI control panel.
    /// </summary>
    public class StatusStore
    {
        /// <summary>
        /// Creates a status store in the shared application configuration folder.
        /// </summary>
        /// <param name="configDirectory">Directory that contains runtime-status.xml.</param>
        public StatusStore(string configDirectory)
        {
            ConfigDirectory = configDirectory;
            StatusPath = Path.Combine(configDirectory, "runtime-status.xml");
        }

        /// <summary>
        /// Directory that contains the status file.
        /// </summary>
        public string ConfigDirectory { get; private set; }

        /// <summary>
        /// Full path to runtime-status.xml.
        /// </summary>
        public string StatusPath { get; private set; }

        /// <summary>
        /// Loads the most recent service status snapshot.
        /// </summary>
        /// <returns>Stored status or a default status when the file is missing or unreadable.</returns>
        public RuntimeStatus Load()
        {
            try
            {
                if (!File.Exists(StatusPath))
                {
                    return new RuntimeStatus();
                }

                using (var stream = File.OpenRead(StatusPath))
                {
                    var serializer = new XmlSerializer(typeof(RuntimeStatus));
                    var status = serializer.Deserialize(stream) as RuntimeStatus;
                    return status ?? new RuntimeStatus();
                }
            }
            catch
            {
                return new RuntimeStatus
                {
                    ServiceMessage = "Runtime status could not be read."
                };
            }
        }

        /// <summary>
        /// Saves a service status snapshot.
        /// </summary>
        /// <param name="status">Runtime status to persist.</param>
        public void Save(RuntimeStatus status)
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                status.UpdatedAt = DateTime.Now;

                var tempPath = StatusPath + ".tmp";
                using (var stream = File.Create(tempPath))
                {
                    var serializer = new XmlSerializer(typeof(RuntimeStatus));
                    serializer.Serialize(stream, status);
                }

                if (File.Exists(StatusPath))
                {
                    File.Delete(StatusPath);
                }

                File.Move(tempPath, StatusPath);
            }
            catch
            {
                // Runtime status is diagnostic only. Service operation must not fail because of it.
            }
        }
    }
}

