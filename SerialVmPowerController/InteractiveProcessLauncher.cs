using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SerialVmPowerController
{
    /// <summary>
    /// Starts a GUI process in the active interactive Windows session from a Windows Service.
    /// </summary>
    /// <remarks>
    /// Services run in Session 0 and cannot show normal desktop UI directly. This helper asks
    /// Windows for the active console user's token and creates the process on winsta0\default.
    /// </remarks>
    public static class InteractiveProcessLauncher
    {
        private const uint WtsCurrentServerHandle = 0;
        private const uint InvalidSessionId = 0xFFFFFFFF;
        private const uint TokenAssignPrimary = 0x0001;
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenImpersonate = 0x0004;
        private const uint TokenQuery = 0x0008;
        private const uint TokenAdjustDefault = 0x0080;
        private const uint TokenAdjustSessionId = 0x0100;
        private const uint CreateUnicodeEnvironment = 0x00000400;

        /// <summary>
        /// Starts an executable in the active console user's desktop session.
        /// </summary>
        /// <param name="fileName">Executable to launch.</param>
        /// <param name="arguments">Command-line arguments for the executable.</param>
        /// <param name="workingDirectory">Working directory for the process.</param>
        /// <returns>Launch result with success flag and diagnostic message.</returns>
        public static InteractiveProcessLaunchResult Start(string fileName, string arguments, string workingDirectory)
        {
            if (Environment.UserInteractive)
            {
                return StartInCurrentSession(fileName, arguments, workingDirectory);
            }

            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == InvalidSessionId)
            {
                return InteractiveProcessLaunchResult.Fail("No active console session is available.");
            }

            IntPtr userToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;

            try
            {
                if (!WTSQueryUserToken(sessionId, out userToken))
                {
                    return InteractiveProcessLaunchResult.Fail("Cannot query active user token: " + LastWin32ErrorMessage());
                }

                var desiredAccess = TokenAssignPrimary
                    | TokenDuplicate
                    | TokenImpersonate
                    | TokenQuery
                    | TokenAdjustDefault
                    | TokenAdjustSessionId;

                if (!DuplicateTokenEx(userToken, desiredAccess, IntPtr.Zero, 2, 1, out primaryToken))
                {
                    return InteractiveProcessLaunchResult.Fail("Cannot duplicate active user token: " + LastWin32ErrorMessage());
                }

                if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                {
                    environment = IntPtr.Zero;
                }

                var startupInfo = new STARTUPINFO();
                startupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                startupInfo.lpDesktop = @"winsta0\default";

                PROCESS_INFORMATION processInformation;
                var commandLine = Quote(fileName) + " " + (arguments ?? string.Empty);
                var workDir = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetDirectoryName(fileName)
                    : workingDirectory;

                var created = CreateProcessAsUser(
                    primaryToken,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateUnicodeEnvironment,
                    environment,
                    workDir,
                    ref startupInfo,
                    out processInformation);

                if (!created)
                {
                    return InteractiveProcessLaunchResult.Fail("Cannot create process in active session: " + LastWin32ErrorMessage());
                }

                CloseHandle(processInformation.hThread);
                CloseHandle(processInformation.hProcess);

                return InteractiveProcessLaunchResult.Ok("Process started in session " + sessionId + ".");
            }
            finally
            {
                if (environment != IntPtr.Zero)
                {
                    DestroyEnvironmentBlock(environment);
                }

                if (primaryToken != IntPtr.Zero)
                {
                    CloseHandle(primaryToken);
                }

                if (userToken != IntPtr.Zero)
                {
                    CloseHandle(userToken);
                }
            }
        }

        /// <summary>
        /// Starts a process normally when the caller is already interactive.
        /// </summary>
        /// <param name="fileName">Executable to launch.</param>
        /// <param name="arguments">Command-line arguments for the executable.</param>
        /// <param name="workingDirectory">Working directory for the process.</param>
        /// <returns>Launch result with success flag and diagnostic message.</returns>
        private static InteractiveProcessLaunchResult StartInCurrentSession(string fileName, string arguments, string workingDirectory)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments ?? string.Empty,
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                        ? Path.GetDirectoryName(fileName)
                        : workingDirectory,
                    UseShellExecute = false
                };

                Process.Start(startInfo);
                return InteractiveProcessLaunchResult.Ok("Process started in current interactive session.");
            }
            catch (Exception ex)
            {
                return InteractiveProcessLaunchResult.Fail("Cannot start process in current session: " + ex.Message);
            }
        }

        /// <summary>
        /// Quotes a command-line argument.
        /// </summary>
        /// <param name="value">Argument value.</param>
        /// <returns>Quoted argument value.</returns>
        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Returns the last Win32 error as readable text.
        /// </summary>
        /// <returns>Win32 error code and message.</returns>
        private static string LastWin32ErrorMessage()
        {
            var error = Marshal.GetLastWin32Error();
            return error + " - " + new Win32Exception(error).Message;
        }

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr existingToken,
            uint desiredAccess,
            IntPtr tokenAttributes,
            int impersonationLevel,
            int tokenType,
            out IntPtr newToken);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(
            IntPtr token,
            string applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
    }

    /// <summary>
    /// Result returned by <see cref="InteractiveProcessLauncher"/>.
    /// </summary>
    public class InteractiveProcessLaunchResult
    {
        /// <summary>
        /// True when the process was successfully started.
        /// </summary>
        public bool Succeeded { get; set; }

        /// <summary>
        /// Diagnostic message describing the launch result.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Creates a successful launch result.
        /// </summary>
        /// <param name="message">Diagnostic message.</param>
        /// <returns>Successful launch result.</returns>
        public static InteractiveProcessLaunchResult Ok(string message)
        {
            return new InteractiveProcessLaunchResult { Succeeded = true, Message = message };
        }

        /// <summary>
        /// Creates a failed launch result.
        /// </summary>
        /// <param name="message">Diagnostic message.</param>
        /// <returns>Failed launch result.</returns>
        public static InteractiveProcessLaunchResult Fail(string message)
        {
            return new InteractiveProcessLaunchResult { Succeeded = false, Message = message };
        }
    }
}

