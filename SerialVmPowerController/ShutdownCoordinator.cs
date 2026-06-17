using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SerialVmPowerController
{
    /// <summary>
    /// Coordinates the complete CTS-triggered shutdown flow.
    /// </summary>
    /// <remarks>
    /// The soft VMware stop command is allowed to time out. On this installation
    /// it can leave Windows XP at the "safe to turn off" screen while vmrun keeps
    /// waiting, so the coordinator kills only the waiting vmrun process and then
    /// uses a VMware hard stop as the final VM power-off step.
    /// </remarks>
    public class ShutdownCoordinator
    {
        private readonly LogService _log;
        private readonly object _sync = new object();
        private bool _shutdownInProgress;

        /// <summary>
        /// Creates a coordinator that writes all operational events to the provided logger.
        /// </summary>
        /// <param name="log">Logger used by the shutdown sequence.</param>
        public ShutdownCoordinator(LogService log)
        {
            _log = log;
        }

        /// <summary>
        /// True while a CTS-triggered shutdown sequence is already running.
        /// </summary>
        public bool IsShutdownInProgress
        {
            get
            {
                lock (_sync)
                {
                    return _shutdownInProgress;
                }
            }
        }

        /// <summary>
        /// Starts the CTS-triggered shutdown sequence on a background thread.
        /// </summary>
        /// <param name="settings">Snapshot of the settings to use for this sequence.</param>
        /// <param name="isCtsStillActive">Callback used after debounce to confirm CTS is still ON.</param>
        /// <returns>A task representing the background shutdown operation.</returns>
        public Task HandleCtsAsync(AppSettings settings, Func<bool> isCtsStillActive)
        {
            lock (_sync)
            {
                if (_shutdownInProgress)
                {
                    _log.Warning("CTS detected while shutdown sequence is already running.");
                    return Task.FromResult(0);
                }

                _shutdownInProgress = true;
            }

            return Task.Run(delegate
            {
                try
                {
                    RunCtsSequence(settings, isCtsStillActive);
                }
                finally
                {
                    lock (_sync)
                    {
                        _shutdownInProgress = false;
                    }
                }
            });
        }

        /// <summary>
        /// Runs a manual VMware soft stop using the configured soft timeout.
        /// </summary>
        /// <param name="settings">Settings that contain vmrun and VMX paths.</param>
        /// <returns>A task representing the background operation.</returns>
        public Task SoftStopVmAsync(AppSettings settings)
        {
            return Task.Run(delegate
            {
                _log.Info("Manual soft VM stop requested.");
                RunVmStop(settings, "soft", settings.SoftTimeoutSeconds);
            });
        }

        /// <summary>
        /// Runs a manual VMware hard stop using the configured hard timeout.
        /// </summary>
        /// <param name="settings">Settings that contain vmrun and VMX paths.</param>
        /// <returns>A task representing the background operation.</returns>
        public Task HardStopVmAsync(AppSettings settings)
        {
            return Task.Run(delegate
            {
                _log.Info("Manual hard VM stop requested.");
                RunVmStop(settings, "hard", settings.HardTimeoutSeconds);
            });
        }

        /// <summary>
        /// Schedules Windows host shutdown using shutdown.exe.
        /// </summary>
        /// <param name="delaySeconds">Delay before the host shuts down.</param>
        public void ScheduleHostShutdown(int delaySeconds)
        {
            var safeDelay = Math.Max(0, delaySeconds);
            _log.Warning("Scheduling host shutdown in " + safeDelay + " seconds.");

            var result = RunProcess(
                "shutdown.exe",
                "/s /t " + safeDelay + " /c \"Serial VM Power Controller\"",
                10,
                true);

            if (!result.Succeeded)
            {
                _log.Error("Host shutdown command failed. ExitCode=" + FormatExitCode(result) + " Error=" + TrimForLog(result.Error));
            }
        }

        /// <summary>
        /// Checks whether the configured VM is currently listed as running by VMware.
        /// </summary>
        /// <param name="settings">Settings that contain vmrun and VMX paths.</param>
        /// <returns>True when the VM is listed, or when the check fails.</returns>
        public bool IsConfiguredVmRunning(AppSettings settings)
        {
            return IsVmRunning(settings);
        }

        /// <summary>
        /// Executes the full automatic flow after CTS is detected.
        /// </summary>
        /// <param name="settings">Settings snapshot captured when CTS was detected.</param>
        /// <param name="isCtsStillActive">Callback that confirms CTS is still ON after debounce.</param>
        private void RunCtsSequence(AppSettings settings, Func<bool> isCtsStillActive)
        {
            _log.Warning("CTS detected. Debounce started for " + settings.DebounceSeconds + " seconds.");

            if (settings.BlockCtsShutdown)
            {
                _log.Warning("Block CTS shutdown is active. CTS was logged, shutdown skipped.");
                return;
            }

            Thread.Sleep(Math.Max(0, settings.DebounceSeconds) * 1000);
            if (isCtsStillActive != null && !isCtsStillActive())
            {
                _log.Info("CTS returned to OFF during debounce. Shutdown skipped.");
                return;
            }

            if (!ValidateSettings(settings))
            {
                return;
            }

            _log.Warning("Starting VMware soft shutdown.");
            var softStopResult = RunVmStop(settings, "soft", settings.SoftTimeoutSeconds);

            if (ShouldRunHardStopAfterSoftStop(settings, softStopResult))
            {
                RunVmStop(settings, "hard", settings.HardTimeoutSeconds);
            }

            if (settings.ShutdownHostAfterVm)
            {
                ScheduleHostShutdown(settings.HostShutdownDelaySeconds);
            }
            else
            {
                _log.Warning("Host shutdown is disabled in settings.");
            }
        }

        /// <summary>
        /// Decides whether the automatic CTS sequence must continue with a VMware hard stop.
        /// </summary>
        /// <remarks>
        /// Windows XP can reach the "safe to turn off" screen while vmrun soft stop
        /// waits forever or while vmrun list no longer reports the VM. In that state
        /// the VMware/KVM window can still be open, so a timed-out or failed soft stop
        /// is treated as not safely powered off and hard stop is started.
        /// </remarks>
        /// <param name="settings">Settings that contain vmrun and VMX paths.</param>
        /// <param name="softStopResult">Result returned by the VMware soft stop command.</param>
        /// <returns>True when hard stop should be executed.</returns>
        private bool ShouldRunHardStopAfterSoftStop(AppSettings settings, ProcessResult softStopResult)
        {
            if (softStopResult == null)
            {
                _log.Warning("Soft stop result is unknown. Starting hard stop.");
                return true;
            }

            if (softStopResult.TimedOut)
            {
                _log.Warning("Soft stop timed out. Starting hard stop without trusting vmrun list.");
                return true;
            }

            if (!softStopResult.Succeeded)
            {
                _log.Warning("Soft stop did not complete successfully. Starting hard stop.");
                return true;
            }

            if (IsVmRunning(settings))
            {
                _log.Warning("VM is still listed as running after soft stop. Starting hard stop.");
                return true;
            }

            _log.Info("VM is no longer listed as running after successful soft stop.");
            return false;
        }

        /// <summary>
        /// Verifies that required VMware paths exist before a real shutdown is attempted.
        /// </summary>
        /// <param name="settings">Settings to validate.</param>
        /// <returns>True when vmrun.exe and the VMX file are present.</returns>
        private bool ValidateSettings(AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.VmrunPath) || !File.Exists(settings.VmrunPath))
            {
                _log.Error("vmrun.exe was not found: " + settings.VmrunPath);
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.VmxPath) || !File.Exists(settings.VmxPath))
            {
                _log.Error("VMX file was not found: " + settings.VmxPath);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Runs vmrun stop with either soft or hard mode.
        /// </summary>
        /// <param name="settings">Settings that contain vmrun and VMX paths.</param>
        /// <param name="mode">VMware stop mode: soft or hard.</param>
        /// <param name="timeoutSeconds">Maximum time to wait for vmrun.</param>
        /// <returns>Captured result of the vmrun process.</returns>
        private ProcessResult RunVmStop(AppSettings settings, string mode, int timeoutSeconds)
        {
            var args = "stop " + Quote(settings.VmxPath) + " " + mode;
            var result = RunProcess(settings.VmrunPath, args, timeoutSeconds, true);

            if (result.TimedOut)
            {
                _log.Warning("vmrun stop " + mode + " timed out after " + timeoutSeconds + " seconds. vmrun process was killed.");
            }
            else if (result.Succeeded)
            {
                _log.Info("vmrun stop " + mode + " completed successfully.");
            }
            else
            {
                _log.Warning("vmrun stop " + mode + " finished with ExitCode=" + FormatExitCode(result) + " Error=" + TrimForLog(result.Error));
            }

            return result;
        }

        /// <summary>
        /// Checks whether the configured VMX is still present in vmrun list output.
        /// </summary>
        /// <param name="settings">Settings that contain vmrun and VMX paths.</param>
        /// <returns>True when the VM is listed, or when the list command fails.</returns>
        private bool IsVmRunning(AppSettings settings)
        {
            _log.Info("Checking VMware running VM list.");
            var result = RunProcess(settings.VmrunPath, "list", 15, true);

            if (!result.Succeeded)
            {
                _log.Warning("vmrun list failed. Assuming VM can still be running. ExitCode=" + FormatExitCode(result));
                return true;
            }

            var output = result.Output ?? string.Empty;
            var running = output.IndexOf(settings.VmxPath, StringComparison.OrdinalIgnoreCase) >= 0;
            _log.Info(running ? "Configured VM is still listed as running." : "Configured VM is not listed as running.");
            return running;
        }

        /// <summary>
        /// Runs an external process with captured output and a timeout.
        /// </summary>
        /// <param name="fileName">Executable to start.</param>
        /// <param name="arguments">Command-line arguments for the executable.</param>
        /// <param name="timeoutSeconds">Maximum time to wait before timing out.</param>
        /// <param name="killOnTimeout">Whether the process should be killed after timeout.</param>
        /// <returns>Process exit code, timeout flag and captured output.</returns>
        private static ProcessResult RunProcess(string fileName, string arguments, int timeoutSeconds, bool killOnTimeout)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            var outputSync = new object();

            var result = new ProcessResult
            {
                Output = string.Empty,
                Error = string.Empty
            };

            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
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
                        lock (outputSync)
                        {
                            output.AppendLine(e.Data);
                        }
                    }
                };

                process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (outputSync)
                        {
                            error.AppendLine(e.Data);
                        }
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var timeout = Math.Max(1, timeoutSeconds) * 1000;
                if (!process.WaitForExit(timeout))
                {
                    result.TimedOut = true;
                    if (killOnTimeout)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // The process may have exited between timeout and Kill.
                        }
                    }
                }
                else
                {
                    result.ExitCode = process.ExitCode;
                }

                try
                {
                    process.WaitForExit(2000);
                    if (result.ExitCode == null && process.HasExited)
                    {
                        result.ExitCode = process.ExitCode;
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }
            }

            lock (outputSync)
            {
                result.Output = output.ToString();
                result.Error = error.ToString();
            }
            return result;
        }

        /// <summary>
        /// Quotes a command-line argument for the simple vmrun command lines used here.
        /// </summary>
        /// <param name="value">Argument value to quote.</param>
        /// <returns>Quoted argument value.</returns>
        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Formats the process exit code for compact log output.
        /// </summary>
        /// <param name="result">Process result to format.</param>
        /// <returns>Numeric exit code or "none".</returns>
        private static string FormatExitCode(ProcessResult result)
        {
            return result.ExitCode.HasValue ? result.ExitCode.Value.ToString() : "none";
        }

        /// <summary>
        /// Trims long process error text so a single log entry remains readable.
        /// </summary>
        /// <param name="value">Raw error text.</param>
        /// <returns>Single-line text capped to a short length.</returns>
        private static string TrimForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            value = value.Replace(Environment.NewLine, " ").Trim();
            if (value.Length > 300)
            {
                value = value.Substring(0, 300) + "...";
            }

            return value;
        }
    }
}

