using System;
using System.IO.Ports;
using System.Timers;

namespace SerialVmPowerController
{
    /// <summary>
    /// Opens a Windows serial port and reports CTS state changes.
    /// </summary>
    /// <remarks>
    /// CTS is polled as well as monitored through PinChanged because some
    /// USB/serial drivers do not raise pin events reliably in all situations.
    /// </remarks>
    public class SerialMonitor : IDisposable
    {
        private readonly object _sync = new object();
        private SerialPort _port;
        private Timer _timer;
        private bool _lastCts;

        /// <summary>
        /// Raised when CTS changes value.
        /// </summary>
        /// <remarks>
        /// The argument is true for CTS ON and false for CTS OFF.
        /// </remarks>
        public event Action<bool> CtsChanged;

        /// <summary>
        /// Raised when reading the serial port fails after monitoring has started.
        /// </summary>
        public event Action<string> Error;

        /// <summary>
        /// True while the COM port is open and CTS monitoring is active.
        /// </summary>
        public bool IsMonitoring { get; private set; }

        /// <summary>
        /// Last known CTS state.
        /// </summary>
        public bool CurrentCts
        {
            get
            {
                lock (_sync)
                {
                    return _lastCts;
                }
            }
        }

        /// <summary>
        /// Opens the configured COM port and starts CTS monitoring.
        /// </summary>
        /// <param name="portName">Windows COM port name, for example COM3.</param>
        /// <param name="enableDtr">Whether to drive the DTR output line while the port is open.</param>
        /// <param name="enableRts">Whether to drive the RTS output line while the port is open.</param>
        public void Start(string portName, bool enableDtr, bool enableRts)
        {
            Stop();

            var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One);
            port.Handshake = Handshake.None;
            port.DtrEnable = enableDtr;
            port.RtsEnable = enableRts;
            port.PinChanged += OnPinChanged;
            port.Open();

            lock (_sync)
            {
                _port = port;
                _lastCts = port.CtsHolding;
                IsMonitoring = true;
            }

            _timer = new Timer(250);
            _timer.AutoReset = true;
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }

        /// <summary>
        /// Stops monitoring and releases the COM port.
        /// </summary>
        public void Stop()
        {
            Timer timerToDispose = null;
            SerialPort portToDispose = null;

            lock (_sync)
            {
                if (_timer != null)
                {
                    timerToDispose = _timer;
                    _timer = null;
                }

                if (_port != null)
                {
                    portToDispose = _port;
                    _port = null;
                }

                IsMonitoring = false;
                _lastCts = false;
            }

            if (timerToDispose != null)
            {
                timerToDispose.Stop();
                timerToDispose.Elapsed -= OnTimerElapsed;
                timerToDispose.Dispose();
            }

            if (portToDispose != null)
            {
                portToDispose.PinChanged -= OnPinChanged;
                portToDispose.Dispose();
            }
        }

        /// <summary>
        /// Releases the serial port and polling timer.
        /// </summary>
        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Polling timer callback used as a reliable fallback for CTS changes.
        /// </summary>
        /// <param name="sender">Timer instance that raised the event.</param>
        /// <param name="e">Timer event data.</param>
        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            PollCts();
        }

        /// <summary>
        /// Serial pin notification callback from the Windows serial driver.
        /// </summary>
        /// <param name="sender">Serial port that raised the event.</param>
        /// <param name="e">Pin change event data.</param>
        private void OnPinChanged(object sender, SerialPinChangedEventArgs e)
        {
            if (e.EventType == SerialPinChange.CtsChanged)
            {
                PollCts();
            }
        }

        /// <summary>
        /// Reads CTS, stores the new state and raises <see cref="CtsChanged"/> when it changed.
        /// </summary>
        private void PollCts()
        {
            bool newValue;
            bool changed = false;

            try
            {
                lock (_sync)
                {
                    if (_port == null || !_port.IsOpen)
                    {
                        return;
                    }

                    newValue = _port.CtsHolding;
                    if (newValue != _lastCts)
                    {
                        _lastCts = newValue;
                        changed = true;
                    }
                }

                if (changed)
                {
                    var handler = CtsChanged;
                    if (handler != null)
                    {
                        handler(newValue);
                    }
                }
            }
            catch (Exception ex)
            {
                var handler = Error;
                if (handler != null)
                {
                    handler(ex.Message);
                }
            }
        }
    }
}

