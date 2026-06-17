using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SerialVmPowerController
{
    /// <summary>
    /// Writes timestamped operational log lines to disk and exposes helpers for reading the shared log file.
    /// </summary>
    public class LogService
    {
        private readonly object _sync = new object();

        /// <summary>
        /// Creates a logger that stores log.txt in the provided configuration folder.
        /// </summary>
        /// <param name="configDirectory">Directory used for the persistent log file.</param>
        public LogService(string configDirectory)
        {
            ConfigDirectory = configDirectory;
            LogPath = Path.Combine(configDirectory, "log.txt");
        }

        /// <summary>
        /// Raised after this process writes a new line, even if writing it to disk fails.
        /// </summary>
        public event Action<string> LineWritten;

        /// <summary>
        /// Directory that contains the log file.
        /// </summary>
        public string ConfigDirectory { get; private set; }

        /// <summary>
        /// Full path to log.txt.
        /// </summary>
        public string LogPath { get; private set; }

        /// <summary>
        /// Writes an informational log entry.
        /// </summary>
        /// <param name="message">Message text to append.</param>
        public void Info(string message)
        {
            Write("INFO", message);
        }

        /// <summary>
        /// Writes a warning log entry.
        /// </summary>
        /// <param name="message">Message text to append.</param>
        public void Warning(string message)
        {
            Write("WARN", message);
        }

        /// <summary>
        /// Writes an error log entry.
        /// </summary>
        /// <param name="message">Message text to append.</param>
        public void Error(string message)
        {
            Write("ERROR", message);
        }

        /// <summary>
        /// Reads the last lines from the current log file for display on startup.
        /// </summary>
        /// <param name="maxLines">Maximum number of lines to return.</param>
        /// <returns>Log tail ordered from oldest to newest.</returns>
        public IEnumerable<string> ReadTailLines(int maxLines)
        {
            try
            {
                if (!File.Exists(LogPath))
                {
                    return Enumerable.Empty<string>();
                }

                return ReadAllLinesShared().Reverse().Take(maxLines).Reverse().ToArray();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Returns the current log file length without locking out the service writer.
        /// </summary>
        /// <returns>Current log size in bytes, or 0 when the file is missing or unreadable.</returns>
        public long GetLength()
        {
            try
            {
                if (!File.Exists(LogPath))
                {
                    return 0;
                }

                using (var stream = OpenLogForSharedRead())
                {
                    return stream.Length;
                }
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Reads new log text starting at a previous file position.
        /// </summary>
        /// <param name="position">Previous byte position in the log file.</param>
        /// <param name="newPosition">Updated byte position after the read.</param>
        /// <returns>New text appended to the log file since <paramref name="position"/>.</returns>
        public string ReadFrom(long position, out long newPosition)
        {
            newPosition = position;

            try
            {
                if (!File.Exists(LogPath))
                {
                    newPosition = 0;
                    return string.Empty;
                }

                using (var stream = OpenLogForSharedRead())
                {
                    if (position > stream.Length)
                    {
                        position = 0;
                    }

                    stream.Seek(position, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        var text = reader.ReadToEnd();
                        newPosition = stream.Position;
                        return text;
                    }
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Reads all log lines while allowing the service to keep appending to the file.
        /// </summary>
        /// <returns>All log lines currently readable from disk.</returns>
        private string[] ReadAllLinesShared()
        {
            using (var stream = OpenLogForSharedRead())
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd()
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .Where(line => line.Length > 0)
                    .ToArray();
            }
        }

        /// <summary>
        /// Opens log.txt for read access without blocking the service writer.
        /// </summary>
        /// <returns>A readable file stream with shared read/write/delete access.</returns>
        private FileStream OpenLogForSharedRead()
        {
            return new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        /// <summary>
        /// Formats and writes a log line while keeping file access thread-safe.
        /// </summary>
        /// <param name="level">Short log level label.</param>
        /// <param name="message">Message text to append.</param>
        private void Write(string level, string message)
        {
            var line = string.Format("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}", DateTime.Now, level, message);

            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(ConfigDirectory);
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                catch
                {
                    // The UI still receives the log line even if the disk is unavailable.
                }
            }

            var handler = LineWritten;
            if (handler != null)
            {
                handler(line);
            }
        }
    }
}

