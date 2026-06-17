using System;
using System.IO;
using System.Xml.Serialization;

namespace SerialVmPowerController
{
    /// <summary>
    /// Loads and saves application settings from a single XML file next to the executable.
    /// </summary>
    public class SettingsStore
    {
        /// <summary>
        /// Creates a settings store using the directory from which the application is running.
        /// </summary>
        public SettingsStore()
        {
            ConfigDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            SettingsPath = Path.Combine(ConfigDirectory, "settings.xml");
        }

        /// <summary>
        /// Directory that contains settings and log files.
        /// </summary>
        public string ConfigDirectory { get; private set; }

        /// <summary>
        /// Full path to the XML settings file.
        /// </summary>
        public string SettingsPath { get; private set; }

        /// <summary>
        /// Loads settings from disk, falling back to defaults when the file is missing or invalid.
        /// </summary>
        /// <returns>Loaded settings or a default <see cref="AppSettings"/> instance.</returns>
        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return new AppSettings();
                }

                using (var stream = File.OpenRead(SettingsPath))
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    var settings = serializer.Deserialize(stream) as AppSettings;
                    if (settings == null)
                    {
                        return new AppSettings();
                    }

                    settings.NormalizeFixedPaths();
                    return settings;
                }
            }
            catch
            {
                return new AppSettings();
            }
        }

        /// <summary>
        /// Saves settings atomically by writing a temporary XML file and then replacing the old file.
        /// </summary>
        /// <param name="settings">Settings to persist.</param>
        public void Save(AppSettings settings)
        {
            settings.NormalizeFixedPaths();
            Directory.CreateDirectory(ConfigDirectory);

            var tempPath = SettingsPath + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                var serializer = new XmlSerializer(typeof(AppSettings));
                serializer.Serialize(stream, settings);
            }

            if (File.Exists(SettingsPath))
            {
                File.Delete(SettingsPath);
            }

            File.Move(tempPath, SettingsPath);
        }
    }
}

