using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text;

namespace mac {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {
        private BackgroundWorker _infoWorker;
        private const int InfoInterval = 1000;                      // ms between info/graph updates
        private const int InfoTrafficDataCount = 400;               // interval x data count = time duration of graph
        
        private BackgroundWorker _loginWorker;
        private const int LoginInterval = 5000;                     // ms between login attempts

        private DispatcherTimer _textChangedTimer;                  // delays TextChanged handler for mb/minutes textboxxys

        InternetAccess _internetAccess = new InternetAccess();

        private readonly List<WrapNetworkInterface> _networkInterface = new List<WrapNetworkInterface>();
        private WrapNetworkInterface _selectedNetworkInterface;

        private bool _debugMode = true;
        private const string DebugFilename = "C:\\mac.debug";

        // meh, this is in the xaml too, any way to just get it from there?
        private readonly System.Windows.Media.Brush _bTrafficReceived = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x13, 0x3C, 0xAC));
        private readonly System.Windows.Media.Brush _bTrafficSent = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xC6, 0x18));

        private readonly Icon _iconAutoEnabled = new Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/mac.ico")).Stream);
        private readonly Icon _iconAutoDisabled = new Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/macDisabled.ico")).Stream);
        NotifyIcon _notifyIcon;
        private WindowState _storedWindowState = WindowState.Normal;

        private readonly ObservableCollection<KeyValuePair<DateTime, int>> _infoTrafficSent = new ObservableCollection<KeyValuePair<DateTime, int>>();
        private readonly ObservableCollection<KeyValuePair<DateTime, int>> _infoTrafficReceived = new ObservableCollection<KeyValuePair<DateTime, int>>();
        private readonly ObservableCollection<ObservableCollection<KeyValuePair<DateTime, int>>> _infoTrafficData = new ObservableCollection<ObservableCollection<KeyValuePair<DateTime, int>>>();

        private readonly Random _random = new Random();

        private string FormatBytes(double bytes) {
            if (bytes > 1024 * 1024 * 1024)
                return (bytes / (1024 * 1024 * 1024)).ToString("F2") + "GB";
            else if (bytes > 1024 * 1024)
                return (bytes / (1024 * 1024)).ToString("F1") + "MB";
            else if (bytes > 1024)
                return (bytes / 1024).ToString("F0") + "kB";
            else
                return bytes.ToString("F0") + "B";
        }

        /// <summary>
        /// Repopulate the lists used by the graph with all zeros, graph looks wonky if it has to grow 
        /// from zero data points and re-plot itself every new data point
        /// </summary>
        private void RestartGraph() {
            _infoTrafficSent.Clear();
            _infoTrafficReceived.Clear();

            for (int i = 0; i < InfoTrafficDataCount; i++) {
                var fakeDateTime = DateTime.Now.AddSeconds((double)-i * InfoInterval / 1000);
                _infoTrafficSent.Add(new KeyValuePair<DateTime, int>(fakeDateTime, 0));
                _infoTrafficReceived.Add(new KeyValuePair<DateTime, int>(fakeDateTime, 0));
            }

            _infoTrafficData.Clear();
            _infoTrafficData.Add(_infoTrafficReceived);
            _infoTrafficData.Add(_infoTrafficSent);
        }
           
        /// <summary>
        /// Assign a random MAC address to the current interface and restart both the network
        /// interface and the login console
        /// </summary>
        private void RestartInterface() {
            // random mac address
            var bytes = new byte[6];
            _random.NextBytes(bytes);
            bytes[0] = 0x02; // win7 work around
            var physicalAddress = new PhysicalAddress(bytes.ToArray());

            if (_notifyIcon != null && _notifyIcon.Text.Length != 0 && _notifyIcon.Text.IndexOf(Environment.NewLine) > 0) {
                var usage = _notifyIcon.Text.Substring(_notifyIcon.Text.IndexOf(Environment.NewLine) + Environment.NewLine.Length);
                _internetAccess.AddProgress("Restarting interface, usage was '" + usage + "'");
            }

            try {
                _selectedNetworkInterface.PhysicalAddress = physicalAddress.ToString();
                _selectedNetworkInterface.RestartInterface();
            } catch (System.Security.SecurityException) {
                if (_notifyIcon != null) {
                    _notifyIcon.BalloonTipText = "Interface restart failed, no administrator privs?";
                    _notifyIcon.ShowBalloonTip(250);
                }

                return;
            }

            // it worked?
            if (_notifyIcon != null) {
                _notifyIcon.BalloonTipText = "Restarted " + _selectedNetworkInterface.Name;
                _notifyIcon.ShowBalloonTip(250);
            }

            // restart graph as well
            // RestartGraph();

            // log back in to restore internet access
            Login();
        }

        /// <summary>
        /// Updates info section (traffic graph, current usage).  If auto restarts are enabled
        /// then also RestartInterface (change the mac addr and bounce the iface) when one of 
        /// the limits is crossed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void InfoDoWork(object sender, DoWorkEventArgs e) {
            var worker = sender as BackgroundWorker;
            if (worker == null) // is this possible?
                return;

            var args = (object[]) e.Argument;
            var autoEnabled = (bool) args[0];
            var pAutoMinutes = (string) args[1];
            var pAutoMegabytes = (string) args[2];

            var workerStarted = DateTime.Now;

            while (true) {
                if (worker.CancellationPending) {
                    e.Cancel = true;
                    return;
                }

                if (_selectedNetworkInterface == null) {
                    Thread.Sleep(InfoInterval);
                    continue;
                }

                var minutes = (DateTime.Now - _selectedNetworkInterface.LastRestart).TotalMinutes;
                var autoMinutes = 0.00;
                double.TryParse(pAutoMinutes, out autoMinutes);
                var minutesPercent = 0.00;
                if (autoMinutes > 0)
                    minutesPercent = minutes / autoMinutes * 100;

                var megabytes = (_selectedNetworkInterface.BytesReceived + _selectedNetworkInterface.BytesSent) / 1024 / 1024;
                var autoMegabytes = 0.00;
                double.TryParse(pAutoMegabytes, out autoMegabytes);
                var megabytesPercent = 0.00;
                if (autoMegabytes > 0)
                    megabytesPercent = megabytes / autoMegabytes * 100;

                // populate the details, progress bar, line chart
                double[] rate = _selectedNetworkInterface.Rate;
                var rateReceived = (int)rate[0];
                var rateSent = (int)rate[1];

                var infoDetails = string.Format("{0}{1}{2}", _selectedNetworkInterface.Name, Environment.NewLine, _selectedNetworkInterface.PhysicalAddress);
                var infoDetailsSent = string.Format("{0} sent ({1}/s)", FormatBytes(_selectedNetworkInterface.BytesSent), FormatBytes(rateSent));
                var infoDetailsReceived = string.Format("{0} recv ({1}/s)", FormatBytes(_selectedNetworkInterface.BytesReceived), FormatBytes(rateReceived));

                var sb = new StringBuilder();
                sb.Append("up for ");
                sb.Append((DateTime.Now - _selectedNetworkInterface.LastRestart).ToString(@"hh\:mm\:ss"));
                if (autoEnabled && autoMinutes > 0) {
                    sb.Append(" (");
                    sb.Append(minutesPercent.ToString("F0"));
                    sb.Append("%)");
                }
                sb.Append(", ");
                sb.Append(FormatBytes(_selectedNetworkInterface.BytesReceived + _selectedNetworkInterface.BytesSent));
                if (autoEnabled && autoMegabytes > 0) {
                    sb.Append(" (");
                    sb.Append(megabytesPercent.ToString("F0"));
                    sb.Append("%)");
                }
                var infoDetailsTotal = sb.ToString();

                this.Dispatcher.Invoke((Action)(() => {
                    txInfoDetails.Text = infoDetails + Environment.NewLine;

                    // color with the same colors used in the linechart (bTrafficSent, bTrafficReceived)
                    var tr1 = new TextRange(txInfoDetails.ContentEnd, txInfoDetails.ContentEnd)
                                  {Text = infoDetailsSent + " "};
                    tr1.ApplyPropertyValue(TextElement.ForegroundProperty, _bTrafficSent);

                    var tr2 = new TextRange(txInfoDetails.ContentEnd, txInfoDetails.ContentEnd)
                                  {Text = infoDetailsReceived + Environment.NewLine};
                    tr2.ApplyPropertyValue(TextElement.ForegroundProperty, _bTrafficReceived);

                    // add new data points, remove old data points
                    DateTime now = DateTime.Now;

                    _infoTrafficSent.Add(new KeyValuePair<DateTime, int>(now, rateSent));
                    while (_infoTrafficSent.Count > InfoTrafficDataCount)
                        _infoTrafficSent.RemoveAt(0);
                    _infoTrafficReceived.Add(new KeyValuePair<DateTime, int>(now, rateReceived));
                    while (_infoTrafficReceived.Count > InfoTrafficDataCount)
                        _infoTrafficReceived.RemoveAt(0);

                    if (_notifyIcon != null) {
                        // meh length limit
                        if (_selectedNetworkInterface.Name.Length > 60 - infoDetailsTotal.Length)
                            _notifyIcon.Text = string.Format("{0}{1}{2}{3}", 
                                _selectedNetworkInterface.Name.Substring(0, 60 - infoDetailsTotal.Length - 1), 
                                ":", Environment.NewLine, infoDetailsTotal);
                        else
                            _notifyIcon.Text = string.Format("{0}{1}{2}{3}",
                                _selectedNetworkInterface.Name,
                                ":", Environment.NewLine, infoDetailsTotal);
                    }

                    txInfoProgress.Text = infoDetailsTotal;

                    // use the parent stackpanels width as 100% as that's what's making this area as wide as it is
                    if (autoMegabytes > 0 && autoMinutes > 0) {
                        gpBarMinutes.Width = (double)(sInfo.Width * Math.Min(minutesPercent, 100) / 100);
                        gpBarMinutes.Background = System.Windows.Media.Brushes.Khaki;
                        gpBarMegabytes.Width = (double)(sInfo.Width * Math.Min(megabytesPercent, 100) / 100);
                        gpBarMegabytes.Background = System.Windows.Media.Brushes.LightBlue;
                    } 
                    else if (autoMegabytes > 0) {
                        // only one limit being checked so both bars same color/width
                        gpBarMinutes.Width = (double)(sInfo.Width * Math.Min(megabytesPercent, 100) / 100);
                        gpBarMegabytes.Width = (double)(sInfo.Width * Math.Min(megabytesPercent, 100) / 100);
                        gpBarMinutes.Background = System.Windows.Media.Brushes.LightBlue;
                        gpBarMegabytes.Background = System.Windows.Media.Brushes.LightBlue;
                    }
                    else if (autoMinutes > 0) {
                        // only one limit being checked so both bars same color/width
                        gpBarMinutes.Width = (double)(sInfo.Width * Math.Min(minutesPercent, 100) / 100);
                        gpBarMegabytes.Width = (double)(sInfo.Width * Math.Min(minutesPercent, 100) / 100);
                        gpBarMinutes.Background = System.Windows.Media.Brushes.Khaki;
                        gpBarMegabytes.Background = System.Windows.Media.Brushes.Khaki;
                    } 
                    else {
                        gpBarMinutes.Width = 0;
                        gpBarMegabytes.Width = 0;
                    }

                    // 5 second delay after starting the worker before we bounce any interfaces
                    if ((autoMinutes > 0 && minutesPercent > 100) || (autoMegabytes > 0 && megabytesPercent > 100))
                        if (autoEnabled && (DateTime.Now - workerStarted).Seconds > 5)
                            RestartInterface();
                }));

                // try {
                    Thread.Sleep(InfoInterval);
                // }
                // catch { // FIXME this should just catch the parent is dead exception?
                //    return;
                // }
            }
        }

        public void Info() {
            if (_infoWorker != null)
                _infoWorker.CancelAsync();

            var autoMinutes = "";
            if (tbAutoMinutes != null && tbAutoMinutes.Text != null)
                autoMinutes = tbAutoMinutes.Text;

            var autoMegabytes = "";
            if (tbAutoMegabytes != null && tbAutoMegabytes.Text != null)
                autoMegabytes = tbAutoMegabytes.Text;

            _infoWorker = new BackgroundWorker {WorkerSupportsCancellation = true};
            _infoWorker.DoWork += InfoDoWork;
            _infoWorker.RunWorkerAsync(new object[] { (bool)chbAutoEnabled.IsChecked, autoMinutes, autoMegabytes });
        }

        /// <summary>
        /// Checks for internet access.  If we have limited internet access (web requests are
        /// redirected to a login page) then try to log in to get full access
        /// </summary>
        public void LoginDoWork(object sender, DoWorkEventArgs e) {
            var worker = sender as BackgroundWorker;
            if (worker == null) // is this possible?
                return;

            // keep trying to login until we have internet access
            _internetAccess.AddProgress("Checking internet state");

            while (true) {
                if (worker.CancellationPending) {
                    _internetAccess.AddProgress("Manually cancelled, all done");
                    e.Cancel = true;
                    return;
                }

                InternetAccess.State state = _internetAccess.GetState();

                // check again so it doesn't feel unresponsive.. bad net can mean 20+ seconds in each step
                if (worker.CancellationPending) {
                    _internetAccess.AddProgress("Manually cancelled, all done");
                    e.Cancel = true;
                    return;
                }

                if (state == InternetAccess.State.Online) {
                    _internetAccess.AddProgress("Online, all done");
                    return;
                } 
                else if (state == InternetAccess.State.Offline) {
                    _internetAccess.AddProgress("Offline, sleeping ... ");
                } 
                else if (state == InternetAccess.State.Limited) {
                    switch (_internetAccess.Login()) {
                        case LoginResult.OK:
                            _internetAccess.AddProgress("Login successful (" + _internetAccess.ProviderName + "), all done");

                            _notifyIcon.BalloonTipText = "Connected to " + _internetAccess.ProviderName;
                            _notifyIcon.ShowBalloonTip(250);

                            this.Dispatcher.Invoke((Action)(() => {
                                tbAutoMegabytes.Text = _internetAccess.ProviderAutoMegabytes.ToString();
                                tbAutoMinutes.Text = _internetAccess.ProviderAutoMinutes.ToString();
                            }));

                            _selectedNetworkInterface.LastRestart = DateTime.Now;

                            return;
                        case LoginResult.Fail:
                            _internetAccess.AddProgress("Login failed, will retry soon ...");
                            break;
                        case LoginResult.Cancel:
                            // hopefully whatever cancelled left a suitable message
                            return;
                    }
                }

                Thread.Sleep(LoginInterval);
            }
        }

        public void Login() {
            if (_loginWorker != null && _loginWorker.IsBusy)
                return;

            _loginWorker = new BackgroundWorker {WorkerSupportsCancellation = true};
            _loginWorker.DoWork += LoginDoWork;
            _loginWorker.RunWorkerAsync();
        }

        /// <summary>
        /// Get rid of trayicon on MainWindow.OnClosej
        /// </summary>
        void OnClose(object sender, CancelEventArgs ea) {
            if (_notifyIcon != null)
                _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        /// <summary>
        /// Hide taskbar entry when MainWindow is minimized
        /// </summary>
        void OnStateChanged(object sender, EventArgs ea) {
            // take it out of taskbar when it's minimized, trayicon will do
            if (WindowState == WindowState.Minimized)
                Hide();
        }

        /// <summary>
        /// Minimize/Restore MainWindow when clicking the trayicon, restore window when we come
        /// back from being minimized
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="ea"></param>
        void NotifyIconMouseClick(object sender, System.Windows.Forms.MouseEventArgs ea) {
            // left click minimize/restore window to tray icon
            if (ea.Button == MouseButtons.Left) {
                if (WindowState == WindowState.Minimized) {
                    Show();
                    WindowState = _storedWindowState;
                } 
                else {
                    _storedWindowState = WindowState;
                    WindowState = WindowState.Minimized;
                }
            }
            // right click toggle the automatic interface restart
            else if (ea.Button == MouseButtons.Right) {
                chbAutoEnabled.IsChecked = !chbAutoEnabled.IsChecked;
            }
        }

        private void ToggleDebug() {
            _debugMode = !_debugMode;

            _internetAccess.DebugFilename = _debugMode ? DebugFilename : "";

            if (_debugMode) {
                _notifyIcon.BalloonTipText = "Debug enabled, writing to " + DebugFilename;
                _notifyIcon.ShowBalloonTip(250);
            } 
            else {
                _notifyIcon.BalloonTipText = "Debug disabled";
                _notifyIcon.ShowBalloonTip(250);
            }
        }

        private void WindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
            if (e.Key != Key.System)
                return;

            switch (e.SystemKey) {
                // toggle debug (_internetAccess.Login() will write logs to C:\mac.debug)
                case Key.D:
                    ToggleDebug();
                    break;
                // manually disable/enable interface
                case Key.R:
                    RestartInterface();
                    break;
                // manually start a check for internet access
                case Key.C: // check internet access
                    if (_loginWorker != null && _loginWorker.IsBusy)
                        _loginWorker.CancelAsync();
                    else
                        Login();
                    break;
            }
        }

        /// <summary>
        /// Handler for Alt-r manual restart keypress
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void BManualResetClick(object sender, RoutedEventArgs e) {
            if (_selectedNetworkInterface == null) // shouldn't be possible?
                return;

            RestartInterface();
        }

        /// <summary>
        /// Handler for trayicon indicator showing whether auto restarts are enabled or not
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ChbAutoEnabledChanged(object sender, RoutedEventArgs e) {
            if (chbAutoEnabled.IsChecked != null && (bool)chbAutoEnabled.IsChecked)
                _notifyIcon.Icon = _iconAutoEnabled;
            else
                _notifyIcon.Icon = _iconAutoDisabled;

            Info();
        }

        /// <summary>
        /// Handler for selecting a different network interface to focus on
        /// </summary>
        private void CobAutoInterfaceSelectionChanged(object sender, RoutedEventArgs e) {
            var selected = (string)cobAutoInterface.SelectedItem;

            foreach (WrapNetworkInterface wni in _networkInterface) {
                if (wni.Name.Equals(selected)) {
                    _selectedNetworkInterface = wni;
                    break;
                }
            }

            Info();
        }

        /// <summary>
        /// Fetches network interfaces, prepares graph, etc
        /// </summary>
        public void InitializeData() {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) {
                var w = new WrapNetworkInterface(ni);

                // not a real interface, skip it
                if (!w.PhysicalAdapter)
                    continue;

                // try and default to the first wifi interface, any interface will do though
                if (_selectedNetworkInterface == null || w.Name.Equals("Wireless Network Connection"))
                    _selectedNetworkInterface = w;

                cobAutoInterface.Items.Add(w.Name);
                _networkInterface.Add(w);
            }

            cobAutoInterface.SelectedItem = _selectedNetworkInterface.Name;

            // prepop traffic graph with zeros, it looks wonky if it has to grow from no data points
            RestartGraph();

            chInfoTraffic.DataContext = _infoTrafficData;

            // trayicon
            _notifyIcon = new System.Windows.Forms.NotifyIcon {Text = "", Icon = _iconAutoDisabled};
            _notifyIcon.MouseClick += new System.Windows.Forms.MouseEventHandler(NotifyIconMouseClick);
            _notifyIcon.Visible = true;

            _internetAccess.ProgressMaxLines = 10;
                
            _internetAccess.AddProgress("Waiting for something to do (alt-c to force a check)");
            txLoginConsole.DataContext = _internetAccess;

            // delayed OnTextChanged for the mb/minutes textboxes, try to avoid accidental interface
            // restarts when all they were doing was changing one of the auto thresholds
            _textChangedTimer = new DispatcherTimer(DispatcherPriority.Background)
                                   {Interval = new TimeSpan(0, 0, 0, 1, 500)};
            _textChangedTimer.Tick += (o, e) => { _textChangedTimer.Stop(); Info(); };
            tbAutoMegabytes.TextChanged += (o, e) => _textChangedTimer.Start();
            tbAutoMinutes.TextChanged += (o, e) => _textChangedTimer.Start(); 

            Info();
        }

        public MainWindow() {
            InitializeComponent();
            InitializeData();
        }
    }
}
