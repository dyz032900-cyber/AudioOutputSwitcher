using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using FormsTimer = System.Windows.Forms.Timer;

namespace AudioOutputSwitcher
{
    internal static class Program
    {
        private const string InstanceMutexName = @"Local\AudioOutputSwitcher.Instance";
        private const string ActivationPipeName = "AudioOutputSwitcher.ActivatePipe";
        private const string DeferredLaunchArgument = "--deferred-launch";
        private static int handlingFatalError;

        [STAThread]
        private static void Main(string[] args)
        {
            CrashLogger.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                CrashLogger.Write("Fatal startup failure", ex);
                ShowFatalError("Audio Output Switcher could not start.");
                Environment.ExitCode = 1;
            }
        }

        private static void Run(string[] args)
        {
            bool deferredLaunch = args.Length == 1 && string.Equals(args[0], DeferredLaunchArgument, StringComparison.OrdinalIgnoreCase);
            if (args.Length > 0 && !deferredLaunch)
            {
                Environment.ExitCode = ConsoleRunner.Run(args);
                return;
            }

            string configPath = ConfigPathManager.ResolveConfigPath();
            AppSettings startupSettings = AppSettings.Load(configPath);
            if (startupSettings.RunAsAdministrator && !ElevationManager.IsProcessElevated() && !deferredLaunch)
            {
                if (ElevationManager.TryRelaunchElevated())
                {
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using (Mutex instanceMutex = CreateInstanceMutex(deferredLaunch))
            {
                if (instanceMutex == null)
                {
                    TriggerExistingInstance();
                    return;
                }

                Application.Run(new SwitcherApplicationContext(ActivationPipeName));
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            CrashLogger.Write("Unhandled UI thread exception", e.Exception);
            if (Interlocked.Exchange(ref handlingFatalError, 1) == 0)
            {
                ShowFatalError("Audio Output Switcher encountered an unexpected error and will close.");
            }
            Application.Exit();
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;
            CrashLogger.Write("Unhandled process exception", exception);
        }

        private static void ShowFatalError(string message)
        {
            try
            {
                MessageBox.Show(
                    message + Environment.NewLine + Environment.NewLine + "Diagnostic log: " + CrashLogger.LogPath,
                    "Audio Output Switcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
            }
        }

        private static Mutex CreateInstanceMutex(bool waitForRelease)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(waitForRelease ? 5 : 0);
            while (true)
            {
                bool createdNew;
                Mutex mutex = new Mutex(true, InstanceMutexName, out createdNew);
                if (createdNew)
                {
                    return mutex;
                }

                mutex.Dispose();
                if (!waitForRelease || DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                Thread.Sleep(120);
            }
        }

        private static void TriggerExistingInstance()
        {
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out))
                {
                    client.Connect(1500);
                    client.WriteByte(1);
                    client.Flush();
                }
            }
            catch (Exception ex)
            {
                CrashLogger.WriteThrottled("Existing instance activation failed", ex, TimeSpan.FromMinutes(1));
            }
        }
    }

    internal static class CrashLogger
    {
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, DateTime> LastWriteByCategory = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static string logPath;

        public static string LogPath
        {
            get
            {
                EnsureInitialized();
                return logPath;
            }
        }

        public static void Initialize()
        {
            EnsureInitialized();
        }

        public static void Write(string category, Exception exception)
        {
            lock (SyncRoot)
            {
                WriteCore(category, exception);
            }
        }

        public static void Write(string category)
        {
            Write(category, null);
        }

        public static void WriteThrottled(string category, Exception exception, TimeSpan interval)
        {
            lock (SyncRoot)
            {
                DateTime now = DateTime.UtcNow;
                DateTime lastWrite;
                if (LastWriteByCategory.TryGetValue(category, out lastWrite) && (now - lastWrite) < interval)
                {
                    return;
                }

                LastWriteByCategory[category] = now;
                WriteCore(category, exception);
            }
        }

        public static void WriteThrottledAsync(string category, Exception exception, TimeSpan interval)
        {
            lock (SyncRoot)
            {
                DateTime now = DateTime.UtcNow;
                DateTime lastWrite;
                if (LastWriteByCategory.TryGetValue(category, out lastWrite) && (now - lastWrite) < interval)
                {
                    return;
                }
                LastWriteByCategory[category] = now;
            }

            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Write(category, exception);
                });
            }
            catch
            {
            }
        }

        private static void EnsureInitialized()
        {
            lock (SyncRoot)
            {
                if (!string.IsNullOrEmpty(logPath))
                {
                    return;
                }

                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string directory = Path.Combine(root, "AudioOutputSwitcher", "Logs");
                logPath = Path.Combine(directory, "AudioOutputSwitcher.log");
                try
                {
                    Directory.CreateDirectory(directory);
                    RotateIfNeeded();
                }
                catch
                {
                }
            }
        }

        private static void WriteCore(string category, Exception exception)
        {
            try
            {
                EnsureInitialized();
                RotateIfNeeded();

                StringBuilder entry = new StringBuilder();
                entry.AppendLine("--------------------------------------------------------------------------------");
                entry.AppendLine("UTC: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                entry.AppendLine("Category: " + (category ?? "Unknown"));
                entry.AppendLine("Version: " + typeof(CrashLogger).Assembly.GetName().Version);
                entry.AppendLine("Process: " + GetProcessId().ToString());
                entry.AppendLine("Thread: " + Thread.CurrentThread.ManagedThreadId.ToString());
                entry.AppendLine("OS: " + Environment.OSVersion.VersionString);
                entry.AppendLine("64-bit process: " + Environment.Is64BitProcess.ToString());
                entry.AppendLine(exception == null ? "Exception: unavailable" : exception.ToString());
                File.AppendAllText(logPath, entry.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static int GetProcessId()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        private static void RotateIfNeeded()
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath) || new FileInfo(logPath).Length < MaxLogBytes)
            {
                return;
            }

            string previousPath = Path.Combine(Path.GetDirectoryName(logPath), "AudioOutputSwitcher.previous.log");
            if (File.Exists(previousPath))
            {
                File.Delete(previousPath);
            }
            File.Move(logPath, previousPath);
        }
    }

    internal static class ConfigPathManager
    {
        private const string ConfigFileName = "AudioOutputSwitcher.ini";
        private const string PortableMarkerFileName = "AudioOutputSwitcher.portable";

        public static string ResolveConfigPath()
        {
            string legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
            string portableMarker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortableMarkerFileName);
            if (File.Exists(portableMarker))
            {
                return legacyPath;
            }

            string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localRoot))
            {
                CrashLogger.Write("Local application data path was unavailable; continuing with the legacy configuration");
                return legacyPath;
            }

            string targetDirectory = Path.Combine(localRoot, "AudioOutputSwitcher");
            string targetPath = Path.Combine(targetDirectory, ConfigFileName);
            if (File.Exists(targetPath) || !File.Exists(legacyPath))
            {
                return targetPath;
            }

            string temporaryPath = targetPath + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(targetDirectory);
                File.Copy(legacyPath, temporaryPath, false);
                File.Move(temporaryPath, targetPath);
                return targetPath;
            }
            catch (Exception ex)
            {
                if (File.Exists(targetPath))
                {
                    return targetPath;
                }

                CrashLogger.Write("Configuration migration failed; continuing with the legacy configuration", ex);
                return legacyPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    internal static class ConsoleRunner
    {
        public static int Run(string[] args)
        {
            try
            {
                string command = args[0].Trim().ToLowerInvariant();
                if (command == "--list" || command == "list")
                {
                    List<AudioDeviceInfo> devices = AudioManager.GetRenderDevices();
                    for (int i = 0; i < devices.Count; i++)
                    {
                        Console.WriteLine("{0}{1}. {2}", devices[i].IsDefault ? "* " : "  ", i + 1, devices[i].Name);
                    }
                    return 0;
                }

                if (command == "--next" || command == "next")
                {
                    return SwitchRelative(1);
                }

                if (command == "--startup-sync")
                {
                    return SyncStartupMode(args);
                }

                if (command == "--previous" || command == "--prev" || command == "previous" || command == "prev")
                {
                    return SwitchRelative(-1);
                }

                if ((command == "--device" || command == "device") && args.Length >= 2)
                {
                    int index;
                    if (!int.TryParse(args[1], out index) || index < 1)
                    {
                        Console.Error.WriteLine("Device index must be 1 or greater.");
                        return 2;
                    }
                    List<AudioDeviceInfo> devices = AudioManager.GetRenderDevices();
                    if (index > devices.Count)
                    {
                        Console.Error.WriteLine("Device index is not available.");
                        return 2;
                    }
                    AudioManager.SetDefaultRenderDevice(devices[index - 1].Id);
                    Console.WriteLine("Switched to: " + devices[index - 1].Name);
                    return 0;
                }

                if ((command == "--name" || command == "name") && args.Length >= 2)
                {
                    string namePart = args[1];
                    List<AudioDeviceInfo> devices = AudioManager.GetRenderDevices();
                    for (int i = 0; i < devices.Count; i++)
                    {
                        if (devices[i].Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            AudioManager.SetDefaultRenderDevice(devices[i].Id);
                            Console.WriteLine("Switched to: " + devices[i].Name);
                            return 0;
                        }
                    }
                    Console.Error.WriteLine("No device name contains: " + namePart);
                    return 2;
                }

                Console.Error.WriteLine("Usage: AudioOutputSwitcher.exe --list | --next | --previous | --device 1 | --name Headphones");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int SyncStartupMode(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: AudioOutputSwitcher.exe --startup-sync <enable|disable> <standard|highest>");
                return 2;
            }

            bool enabled;
            if (string.Equals(args[1], "enable", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
            }
            else if (string.Equals(args[1], "disable", StringComparison.OrdinalIgnoreCase))
            {
                enabled = false;
            }
            else
            {
                Console.Error.WriteLine("Unknown startup action: " + args[1]);
                return 2;
            }

            bool runAsAdministrator;
            if (string.Equals(args[2], "highest", StringComparison.OrdinalIgnoreCase))
            {
                runAsAdministrator = true;
            }
            else if (string.Equals(args[2], "standard", StringComparison.OrdinalIgnoreCase))
            {
                runAsAdministrator = false;
            }
            else
            {
                Console.Error.WriteLine("Unknown startup mode: " + args[2]);
                return 2;
            }

            StartupManager.ApplyEnabledState(enabled, runAsAdministrator);
            return 0;
        }

        private static int SwitchRelative(int direction)
        {
            List<AudioDeviceInfo> devices = AudioManager.GetRenderDevices();
            if (devices.Count == 0)
            {
                Console.Error.WriteLine("No active output devices.");
                return 2;
            }

            int current = -1;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].IsDefault)
                {
                    current = i;
                    break;
                }
            }

            int next = current < 0 ? 0 : current + direction;
            while (next < 0)
            {
                next += devices.Count;
            }
            next = next % devices.Count;

            AudioManager.SetDefaultRenderDevice(devices[next].Id);
            Console.WriteLine("Switched to: " + devices[next].Name);
            return 0;
        }
    }

    internal sealed class ActivationPipeServer : IDisposable
    {
        private readonly string pipeName;
        private readonly Action onActivate;
        private readonly SynchronizationContext uiContext;
        private readonly Thread workerThread;
        private int stopping;

        public ActivationPipeServer(string pipeName, Action onActivate)
        {
            this.pipeName = pipeName;
            this.onActivate = onActivate;
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            workerThread = new Thread(ListenLoop);
            workerThread.IsBackground = true;
            workerThread.Name = "AudioOutputSwitcher.ActivationPipe";
            workerThread.Start();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref stopping, 1);
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                {
                    client.Connect(200);
                    client.WriteByte(0);
                    client.Flush();
                }
            }
            catch (Exception ex)
            {
                CrashLogger.WriteThrottled("Activation pipe shutdown signal failed", ex, TimeSpan.FromMinutes(1));
            }

            if (workerThread != null && workerThread.IsAlive)
            {
                if (!workerThread.Join(1000))
                {
                    CrashLogger.Write("Activation pipe thread did not stop within one second", null);
                }
            }
        }

        private void ListenLoop()
        {
            while (!IsStopping)
            {
                try
                {
                    using (NamedPipeServerStream server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        if (IsStopping)
                        {
                            break;
                        }

                        server.ReadByte();
                    }

                    if (!IsStopping && onActivate != null)
                    {
                        uiContext.Post(delegate(object state)
                        {
                            if (IsStopping)
                            {
                                return;
                            }

                            try
                            {
                                onActivate();
                            }
                            catch (Exception ex)
                            {
                                CrashLogger.Write("Existing instance activation callback failed", ex);
                            }
                        }, null);
                    }
                }
                catch (Exception ex)
                {
                    if (IsStopping)
                    {
                        break;
                    }

                    CrashLogger.WriteThrottled("Activation pipe listener failed", ex, TimeSpan.FromMinutes(1));
                    Thread.Sleep(100);
                }
            }
        }

        private bool IsStopping
        {
            get { return Interlocked.CompareExchange(ref stopping, 0, 0) != 0; }
        }
    }

    internal sealed class SwitcherApplicationContext : ApplicationContext
    {
        private readonly ActivationPipeServer activationServer;
        private readonly string configPath;
        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip menu;
        private readonly GlobalHotkeyManager hotkeyManager;
        private readonly SwitchToastForm switchToast;
        private readonly FormsTimer dashboardWarmupTimer;
        private readonly FormsTimer powerRecoveryTimer;
        private readonly PowerRecoveryMonitor powerRecoveryMonitor;
        private DashboardForm dashboardForm;
        private AppSettings settings;
        private List<AudioDeviceInfo> devices;

        public SwitcherApplicationContext(string activationPipeName)
        {
            configPath = ConfigPathManager.ResolveConfigPath();
            settings = AppSettings.Load(configPath);
            TryMigrateStartupMode(false);
            devices = new List<AudioDeviceInfo>();
            activationServer = new ActivationPipeServer(
                activationPipeName,
                delegate { OpenDashboard(); });

            menu = new ContextMenuStrip();
            UiTheme.ApplyMenu(menu);
            trayIcon = new NotifyIcon();
            trayIcon.Text = "Audio Output Switcher";
            trayIcon.Icon = AppIcon.Load();
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.MouseUp += OnTrayIconMouseUp;

            hotkeyManager = new GlobalHotkeyManager();
            hotkeyManager.HotkeyPressed += OnHotkeyPressed;
            switchToast = new SwitchToastForm();
            powerRecoveryTimer = new FormsTimer();
            powerRecoveryTimer.Interval = 1200;
            powerRecoveryTimer.Tick += OnPowerRecoveryTimerTick;
            powerRecoveryMonitor = new PowerRecoveryMonitor();
            powerRecoveryMonitor.Suspending += OnSystemSuspending;
            powerRecoveryMonitor.Resumed += BeginPowerRecovery;
            dashboardWarmupTimer = new FormsTimer();
            dashboardWarmupTimer.Interval = 450;
            dashboardWarmupTimer.Tick += delegate
            {
                dashboardWarmupTimer.Stop();
                EnsureDashboardCreated();
                if (dashboardForm != null)
                {
                    dashboardForm.RefreshData();
                }
            };
            dashboardWarmupTimer.Start();

            RefreshMenu();
            ApplyConfiguredHotkeys();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (activationServer != null)
                {
                    activationServer.Dispose();
                }
                if (hotkeyManager != null)
                {
                    hotkeyManager.Dispose();
                }
                if (switchToast != null)
                {
                    switchToast.Dispose();
                }
                if (dashboardWarmupTimer != null)
                {
                    dashboardWarmupTimer.Dispose();
                }
                if (powerRecoveryTimer != null)
                {
                    powerRecoveryTimer.Dispose();
                }
                if (powerRecoveryMonitor != null)
                {
                    powerRecoveryMonitor.Dispose();
                }
                if (dashboardForm != null)
                {
                    dashboardForm.Dispose();
                }
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
                if (menu != null)
                {
                    menu.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void OnSystemSuspending()
        {
            powerRecoveryTimer.Stop();
            hotkeyManager.ResetInputState();
        }

        private void BeginPowerRecovery()
        {
            hotkeyManager.RecoverAfterSystemResume();
            AudioManager.InvalidateRenderDevicesCache();
            powerRecoveryTimer.Stop();
            powerRecoveryTimer.Start();
        }

        private void OnPowerRecoveryTimerTick(object sender, EventArgs e)
        {
            powerRecoveryTimer.Stop();
            if (!hotkeyManager.RecoverAfterSystemResume())
            {
                ShowBalloon("Hotkeys unavailable", hotkeyManager.LastError, ToolTipIcon.Warning);
            }
            AudioManager.InvalidateRenderDevicesCache();
            RefreshMenu();
            if (dashboardForm != null && !dashboardForm.IsDisposed)
            {
                dashboardForm.RefreshData();
            }
        }

        private void OnTrayIconMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenDashboard();
            }
        }

        private void OpenDashboard()
        {
            EnsureDashboardCreated();
            bool refreshStale = dashboardForm.IsRefreshStale(TimeSpan.FromSeconds(8));
            bool refreshedDuringReveal = false;

            if (!dashboardForm.Visible)
            {
                dashboardForm.PrepareForDisplay();
                if (refreshStale)
                {
                    dashboardForm.RefreshData();
                    refreshedDuringReveal = true;
                }
                dashboardForm.Opacity = 0;
                dashboardForm.Show();
                dashboardForm.WindowState = FormWindowState.Normal;
                UiTheme.RevealShownForm(dashboardForm);
            }
            else
            {
                dashboardForm.WindowState = FormWindowState.Normal;
            }
            dashboardForm.BringToFront();
            dashboardForm.Activate();
            if (dashboardForm.Visible && refreshStale && !refreshedDuringReveal)
            {
                dashboardForm.RefreshData();
            }
            try
            {
                NativeMethods.SetForegroundWindow(dashboardForm.Handle);
            }
            catch
            {
            }
        }

        private void EnsureDashboardCreated()
        {
            if (dashboardForm != null && !dashboardForm.IsDisposed)
            {
                return;
            }

            dashboardForm = new DashboardForm(
                delegate { return settings; },
                delegate(bool forceRefresh) { return AudioManager.GetRenderDevices(forceRefresh); },
                delegate(AudioDeviceInfo device) { SwitchToDevice(device); },
                delegate { OpenHotkeySettings(); },
                delegate { RefreshMenu(); });
        }

        private void RefreshMenu()
        {
            menu.Items.Clear();

            try
            {
                devices = AudioManager.GetRenderDevices();
            }
            catch (Exception ex)
            {
                AddDisabledItem("Could not read audio devices");
                menu.Items.Add(new ToolStripSeparator());
                AddMenuItem("Reload", delegate { ReloadEverything(); });
                AddMenuItem("Exit", delegate { ExitThread(); });
                ShowBalloon("Audio Output Switcher", ex.Message, ToolTipIcon.Error);
                return;
            }

            if (devices.Count == 0)
            {
                AddDisabledItem("No active output devices");
            }
            else
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    AudioDeviceInfo device = devices[i];
                    string text = (i + 1).ToString() + ". " + device.Name;
                    ToolStripMenuItem item = new ToolStripMenuItem(text);
                    item.Checked = device.IsDefault;
                    item.Tag = device;
                    UiTheme.ApplyMenuItem(item);
                    item.Click += delegate(object sender, EventArgs args)
                    {
                        ToolStripMenuItem clicked = (ToolStripMenuItem)sender;
                        AudioDeviceInfo target = (AudioDeviceInfo)clicked.Tag;
                        SwitchToDevice(target);
                    };
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem("Next output", delegate { SwitchRelative(1); });
            AddMenuItem("Previous output", delegate { SwitchRelative(-1); });
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem("Hotkey settings...", delegate { OpenHotkeySettings(); });
            AddMenuItem("Open config", delegate { OpenConfig(); });
            AddMenuItem("Reload config", delegate { ReloadEverything(); });
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem("Exit", delegate { ExitThread(); });
        }

        private void AddDisabledItem(string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Enabled = false;
            UiTheme.ApplyMenuItem(item);
            menu.Items.Add(item);
        }

        private void AddMenuItem(string text, EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += handler;
            UiTheme.ApplyMenuItem(item);
            menu.Items.Add(item);
        }

        private void ReloadEverything()
        {
            bool previousRunAsAdministrator = settings.RunAsAdministrator;
            settings = AppSettings.Load(configPath);
            TryMigrateStartupMode(true);
            if (!settings.ShowBalloon)
            {
                switchToast.HideImmediately();
            }
            if (ApplyAdministratorPreferenceChange(previousRunAsAdministrator))
            {
                return;
            }
            RefreshMenu();
            ApplyConfiguredHotkeys();
            ShowBalloon("Audio Output Switcher", "Config reloaded.", ToolTipIcon.Info);
        }

        private void TryMigrateStartupMode(bool notifyFailure)
        {
            try
            {
                StartupManager.TryMigrateEnabledMode(settings.RunAsAdministrator);
            }
            catch (Exception ex)
            {
                CrashLogger.Write("Startup mode migration failed", ex);
                if (notifyFailure && trayIcon != null)
                {
                    ShowBalloon("Start with Windows", "Startup settings could not be repaired. See the diagnostic log for details.", ToolTipIcon.Warning);
                }
            }
        }

        private void OpenHotkeySettings()
        {
            try
            {
                devices = AudioManager.GetRenderDevices(true);
            }
            catch
            {
                devices = new List<AudioDeviceInfo>();
            }

            hotkeyManager.Clear();
            using (HotkeySettingsForm form = new HotkeySettingsForm(settings, devices))
            {
                bool previousRunAsAdministrator = settings.RunAsAdministrator;
                form.PrepareForDisplay();
                UiTheme.AttachRevealOnShown(form);
                if (form.ShowDialog() == DialogResult.OK)
                {
                    settings = form.Settings;
                    settings.Save(configPath);
                    if (!settings.ShowBalloon)
                    {
                        switchToast.HideImmediately();
                    }
                    if (ApplyAdministratorPreferenceChange(previousRunAsAdministrator))
                    {
                        return;
                    }
                    RefreshMenu();
                    ApplyConfiguredHotkeys();
                    if (dashboardForm != null && !dashboardForm.IsDisposed)
                    {
                        dashboardForm.RefreshData();
                    }
                    ShowBalloon("Audio Output Switcher", "Hotkey settings saved.", ToolTipIcon.Info);
                    return;
                }
            }

            ApplyConfiguredHotkeys();
        }

        private bool ApplyAdministratorPreferenceChange(bool previousRunAsAdministrator)
        {
            bool currentRunAsAdministrator = settings != null && settings.RunAsAdministrator;
            bool elevated = ElevationManager.IsProcessElevated();
            if (!previousRunAsAdministrator && currentRunAsAdministrator && !elevated)
            {
                if (ElevationManager.TryRelaunchElevated())
                {
                    ExitThread();
                    return true;
                }

                ShowBalloon("Admin mode", "Admin mode was saved. Approve the UAC prompt next launch to apply it.", ToolTipIcon.Warning);
                return false;
            }

            if (previousRunAsAdministrator && !currentRunAsAdministrator && elevated)
            {
                ShowBalloon("Admin mode", "Admin mode was turned off. Restart the app normally to leave administrator mode.", ToolTipIcon.Info);
            }

            return false;
        }

        private void OpenConfig()
        {
            AppSettings.EnsureExists(configPath);
            ProcessStartInfo startInfo = new ProcessStartInfo(configPath);
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }

        private void OnHotkeyPressed(HotkeyAction action)
        {
            if (action.Kind == HotkeyActionKind.Next)
            {
                SwitchRelative(1);
            }
            else if (action.Kind == HotkeyActionKind.Previous)
            {
                SwitchRelative(-1);
            }
            else if (action.Kind == HotkeyActionKind.DeviceIndex)
            {
                SwitchToIndex(action.DeviceIndex);
            }
            else if (action.Kind == HotkeyActionKind.DeviceNameContains)
            {
                SwitchToName(action.NameContains);
            }
        }

        private void SwitchRelative(int direction)
        {
            try
            {
                devices = AudioManager.GetRenderDevices();
                if (devices.Count == 0)
                {
                    ShowBalloon("Audio Output Switcher", "No active output devices.", ToolTipIcon.Warning);
                    RefreshMenu();
                    return;
                }

                int current = -1;
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].IsDefault)
                    {
                        current = i;
                        break;
                    }
                }

                int next = current < 0 ? 0 : current + direction;
                while (next < 0)
                {
                    next += devices.Count;
                }
                next = next % devices.Count;

                SwitchToDevice(devices[next]);
            }
            catch (Exception ex)
            {
                ShowBalloon("Switch failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private void SwitchToIndex(int zeroBasedIndex)
        {
            try
            {
                devices = AudioManager.GetRenderDevices();
                if (zeroBasedIndex < 0 || zeroBasedIndex >= devices.Count)
                {
                    ShowBalloon("Audio Output Switcher", "Device index is not available.", ToolTipIcon.Warning);
                    RefreshMenu();
                    return;
                }
                SwitchToDevice(devices[zeroBasedIndex]);
            }
            catch (Exception ex)
            {
                ShowBalloon("Switch failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private void SwitchToName(string namePart)
        {
            try
            {
                devices = AudioManager.GetRenderDevices();
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        SwitchToDevice(devices[i]);
                        return;
                    }
                }
                ShowBalloon("Audio Output Switcher", "No device name contains: " + namePart, ToolTipIcon.Warning);
                RefreshMenu();
            }
            catch (Exception ex)
            {
                ShowBalloon("Switch failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private void SwitchToDevice(AudioDeviceInfo device)
        {
            try
            {
                AudioManager.SetDefaultRenderDevice(device.Id);
                RefreshMenu();
                if (dashboardForm != null && !dashboardForm.IsDisposed)
                {
                    dashboardForm.RefreshData();
                }
                ShowSwitchNotification(device.Name);
            }
            catch (Exception ex)
            {
                RefreshMenu();
                ShowBalloon("Switch failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private void ApplyConfiguredHotkeys()
        {
            if (!hotkeyManager.SetBindings(settings.Hotkeys))
            {
                ShowBalloon("Hotkeys unavailable", hotkeyManager.LastError, ToolTipIcon.Warning);
            }
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            bool isErrorOrWarning = icon == ToolTipIcon.Error || icon == ToolTipIcon.Warning;
            if (!settings.ShowBalloon && !isErrorOrWarning)
            {
                return;
            }

            if (isErrorOrWarning)
            {
                CrashLogger.Write(title + " notification", new InvalidOperationException(text));
            }

            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = text;
            trayIcon.BalloonTipIcon = icon;
            trayIcon.ShowBalloonTip(1500);
        }

        private void ShowSwitchNotification(string deviceName)
        {
            if (!settings.ShowBalloon)
            {
                return;
            }

            switchToast.ShowDevice(deviceName);
        }
    }

    internal sealed class AudioDeviceInfo
    {
        public string Id;
        public string Name;
        public bool IsDefault;
    }

    internal static class UiTheme
    {
        private static int fastResizeRenderDepth;
        private const int CachedSurfaceAreaThreshold = 180000;
        public static readonly Color AppBackground = Color.FromArgb(12, 14, 18);
        public static readonly Color Panel = Color.FromArgb(22, 25, 31);
        public static readonly Color PanelElevated = Color.FromArgb(28, 32, 39);
        public static readonly Color Border = Color.FromArgb(56, 62, 73);
        public static readonly Color BorderStrong = Color.FromArgb(78, 88, 104);
        public static readonly Color Text = Color.FromArgb(239, 242, 247);
        public static readonly Color MutedText = Color.FromArgb(150, 160, 176);
        public static readonly Color Accent = Color.FromArgb(99, 170, 255);
        public static readonly Color Hover = Color.FromArgb(39, 45, 55);
        public static readonly Font UiFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        public static readonly Font UiFontBold = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
        public static readonly Font SectionFont = new Font("Segoe UI Semibold", 9.25f, FontStyle.Bold);
        public static readonly Font ItemTitleFont = new Font("Segoe UI Semibold", 10.25f, FontStyle.Bold);
        public static readonly Font SectionTitleFont = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
        public static readonly Font SectionTitleLargeFont = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold);
        public static readonly Font EmphasisFont = new Font("Segoe UI Semibold", 14f, FontStyle.Bold);
        public static readonly Font TitleFont = new Font("Segoe UI Semibold", 16f, FontStyle.Bold);
        public static readonly Font CaptionFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);

        public static void ApplyMenu(ContextMenuStrip menu)
        {
            menu.Renderer = new CodexMenuRenderer();
            menu.BackColor = Panel;
            menu.ForeColor = Text;
            menu.Font = UiFont;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Padding = new Padding(6, 6, 6, 6);
        }

        public static void ApplyMenuItem(ToolStripMenuItem item)
        {
            item.BackColor = Panel;
            item.ForeColor = Text;
            item.AutoSize = true;
            item.Padding = new Padding(8, 6, 12, 6);
            item.Margin = new Padding(0, 1, 0, 1);
        }

        public static void ApplyForm(Form form)
        {
            form.BackColor = AppBackground;
            form.ForeColor = Text;
            form.Font = UiFont;
            ApplyWindowChrome(form);
            form.HandleCreated += delegate { ApplyWindowChrome(form); };
        }

        public static bool TryWarmUpForm(Form form)
        {
            if (form == null || form.IsDisposed || form.Width <= 0 || form.Height <= 0)
            {
                return false;
            }

            try
            {
                if (!form.IsHandleCreated)
                {
                    IntPtr unused = form.Handle;
                }

                form.CreateControl();
                form.PerformLayout();
                form.Update();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void PrimeFormBitmap(Form form)
        {
            if (form == null || form.IsDisposed || form.Width <= 0 || form.Height <= 0)
            {
                return;
            }

            try
            {
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                }
            }
            catch
            {
            }
        }

        public static void AttachRevealOnShown(Form form)
        {
            if (form == null || form.IsDisposed)
            {
                return;
            }

            form.Opacity = 0;
            EventHandler handler = null;
            handler = delegate(object sender, EventArgs e)
            {
                Form shownForm = sender as Form;
                if (shownForm == null)
                {
                    return;
                }

                shownForm.Shown -= handler;
                RevealShownForm(shownForm);
            };
            form.Shown += handler;
        }

        public static void RevealShownForm(Form form)
        {
            if (form == null || form.IsDisposed)
            {
                return;
            }

            form.Invalidate(true);
            form.Update();
            form.Opacity = 1;
        }

        private static void ApplyWindowChrome(Form form)
        {
            try
            {
                int dark = 1;
                NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, Marshal.SizeOf(typeof(int)));
                NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, Marshal.SizeOf(typeof(int)));

                int rounded = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, Marshal.SizeOf(typeof(int)));
            }
            catch
            {
            }
        }

        public static void ApplyLabel(Label label, bool muted)
        {
            label.BackColor = Color.Transparent;
            label.ForeColor = muted ? MutedText : Text;
            label.Font = muted ? CaptionFont : UiFont;
        }

        public static void ApplyButton(Button button, bool primary)
        {
            ThemedButton themedButton = button as ThemedButton;
            if (themedButton != null)
            {
                themedButton.Primary = primary;
                themedButton.ForegroundColor = Text;
                themedButton.Font = UiFontBold;
            }
            else
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = primary ? Accent : Border;
                button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(38, 88, 160) : Hover;
                button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(30, 70, 128) : Color.FromArgb(36, 42, 52);
                button.BackColor = primary ? Color.FromArgb(28, 62, 112) : PanelElevated;
                button.ForeColor = Text;
                button.Font = UiFontBold;
                button.Cursor = Cursors.Hand;
            }
        }

        public static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int safeRadius = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
            int diameter = safeRadius * 2;
            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f));
            float diameter = safeRadius * 2f;
            RectangleF arc = new RectangleF(rect.Location, new SizeF(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void PrepareGraphics(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
        }

        public static void EnableDoubleBuffering(Control control)
        {
            if (control == null)
            {
                return;
            }

            try
            {
                typeof(Control).InvokeMember(
                    "DoubleBuffered",
                    System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null,
                    control,
                    new object[] { true });
            }
            catch
            {
            }
        }

        public static void EnableDoubleBufferingRecursive(Control control)
        {
            if (control == null)
            {
                return;
            }

            EnableDoubleBuffering(control);
            for (int i = 0; i < control.Controls.Count; i++)
            {
                EnableDoubleBufferingRecursive(control.Controls[i]);
            }
        }

        public static int HashState(params int[] values)
        {
            unchecked
            {
                int hash = 17;
                if (values != null)
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        hash = (hash * 31) + values[i];
                    }
                }
                return hash;
            }
        }

        public static Color Blend(Color from, Color to, float amount)
        {
            float t = Math.Max(0f, Math.Min(1f, amount));
            int a = (int)Math.Round(from.A + ((to.A - from.A) * t));
            int r = (int)Math.Round(from.R + ((to.R - from.R) * t));
            int g = (int)Math.Round(from.G + ((to.G - from.G) * t));
            int b = (int)Math.Round(from.B + ((to.B - from.B) * t));
            return Color.FromArgb(a, r, g, b);
        }

        public static void BeginFastResizeRendering()
        {
            fastResizeRenderDepth++;
        }

        public static void EndFastResizeRendering()
        {
            fastResizeRenderDepth = Math.Max(0, fastResizeRenderDepth - 1);
        }

        public static bool IsFastResizeRendering
        {
            get { return fastResizeRenderDepth > 0; }
        }

        public static void DisposeBitmap(ref Bitmap bitmap)
        {
            if (bitmap != null)
            {
                bitmap.Dispose();
                bitmap = null;
            }
        }

        public static Bitmap CreateSupersampledBitmap(Size size, Color clearColor, Action<Graphics> painter)
        {
            if (painter == null || size.Width <= 0 || size.Height <= 0)
            {
                return null;
            }

            int scale = GetSupersampleScale(size);
            Bitmap output = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (Graphics target = Graphics.FromImage(output))
            {
                if (scale <= 1)
                {
                    target.Clear(clearColor);
                    PrepareGraphics(target);
                    painter(target);
                    return output;
                }

                using (Bitmap bitmap = new Bitmap(size.Width * scale, size.Height * scale, PixelFormat.Format32bppPArgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(clearColor);
                    graphics.ScaleTransform(scale, scale);
                    PrepareGraphics(graphics);
                    painter(graphics);

                    target.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    target.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    target.CompositingQuality = CompositingQuality.HighQuality;
                    target.DrawImage(
                        bitmap,
                        new Rectangle(0, 0, size.Width, size.Height),
                        new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                        GraphicsUnit.Pixel);
                }
            }

            return output;
        }

        public static void DrawCachedSupersampled(
            Graphics target,
            Size size,
            Color clearColor,
            ref Bitmap cache,
            ref Size cacheSize,
            ref int cacheToken,
            int stateToken,
            Action<Graphics> painter)
        {
            if (target == null || painter == null || size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            if (IsFastResizeRendering || !ShouldUseCachedSurface(size))
            {
                GraphicsState state = target.Save();
                target.Clear(clearColor);
                PrepareGraphics(target);
                painter(target);
                target.Restore(state);
                return;
            }

            if (cache == null || cacheSize != size || cacheToken != stateToken)
            {
                DisposeBitmap(ref cache);
                cache = CreateSupersampledBitmap(size, clearColor, painter);
                cacheSize = size;
                cacheToken = stateToken;
            }

            if (cache != null)
            {
                target.DrawImageUnscaled(cache, 0, 0);
            }
        }

        private static bool ShouldUseCachedSurface(Size size)
        {
            long area = (long)size.Width * (long)size.Height;
            return area > 0 && area <= CachedSurfaceAreaThreshold;
        }

        private static int GetSupersampleScale(Size size)
        {
            long area = (long)size.Width * (long)size.Height;
            if (area <= 90000 && size.Width <= 520 && size.Height <= 220)
            {
                return 2;
            }

            return 1;
        }

        public static Color ResolveParentBackColor(Control control)
        {
            Control current = control == null ? null : control.Parent;
            while (current != null)
            {
                CardPanel cardPanel = current as CardPanel;
                if (cardPanel != null)
                {
                    return cardPanel.FillColor;
                }
                if (current.BackColor.A > 0)
                {
                    return current.BackColor;
                }
                current = current.Parent;
            }
            return AppBackground;
        }

    }

    internal sealed class CodexMenuRenderer : ToolStripProfessionalRenderer
    {
        public CodexMenuRenderer()
            : base(new CodexColorTable())
        {
            RoundedEdges = true;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (Pen pen = new Pen(UiTheme.Border))
            {
                e.Graphics.DrawRectangle(pen, rect);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle rect = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            Color color = e.Item.Selected ? UiTheme.Hover : UiTheme.Panel;
            using (SolidBrush brush = new SolidBrush(color))
            {
                e.Graphics.FillRectangle(brush, rect);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            int size = 8;
            Rectangle rect = new Rectangle(6, Math.Max(0, (e.Item.Height - size) / 2), size, size);
            using (SolidBrush brush = new SolidBrush(UiTheme.Accent))
            {
                e.Graphics.FillEllipse(brush, rect);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Rectangle textRect = new Rectangle(
                e.TextRectangle.X,
                0,
                e.TextRectangle.Width,
                e.Item.Height);
            ToolStripItemTextRenderEventArgs centeredArgs = new ToolStripItemTextRenderEventArgs(
                e.Graphics,
                e.Item,
                e.Text,
                textRect,
                e.TextColor,
                e.TextFont,
                ContentAlignment.MiddleLeft);
            base.OnRenderItemText(centeredArgs);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen pen = new Pen(Color.FromArgb(42, 48, 58)))
            {
                e.Graphics.DrawLine(pen, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2);
            }
        }
    }

    internal sealed class CodexColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return UiTheme.Panel; } }
        public override Color MenuItemSelected { get { return UiTheme.Hover; } }
        public override Color MenuItemBorder { get { return UiTheme.Hover; } }
        public override Color ImageMarginGradientBegin { get { return UiTheme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return UiTheme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return UiTheme.Panel; } }
        public override Color SeparatorDark { get { return Color.FromArgb(42, 48, 58); } }
        public override Color SeparatorLight { get { return Color.FromArgb(42, 48, 58); } }
        public override Color CheckBackground { get { return UiTheme.Panel; } }
        public override Color CheckSelectedBackground { get { return UiTheme.Hover; } }
        public override Color CheckPressedBackground { get { return UiTheme.Hover; } }
    }

    internal class CompositedForm : Form
    {
        private readonly FormsTimer resizeSettleTimer;
        private bool fastResizeRenderingActive;

        public CompositedForm()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            DoubleBuffered = true;
            UpdateStyles();

            resizeSettleTimer = new FormsTimer();
            resizeSettleTimer.Interval = 120;
            resizeSettleTimer.Tick += delegate
            {
                resizeSettleTimer.Stop();
                EndFastResizeRendering();
            };

            ResizeBegin += delegate { ScheduleFastResizeRendering(); };
            ResizeEnd += delegate { ScheduleFastResizeRendering(); };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Visible && IsHandleCreated)
            {
                ScheduleFastResizeRendering();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                resizeSettleTimer.Dispose();
                if (fastResizeRenderingActive)
                {
                    UiTheme.EndFastResizeRendering();
                    fastResizeRenderingActive = false;
                }
            }
            base.Dispose(disposing);
        }

        private void ScheduleFastResizeRendering()
        {
            if (!Visible || !IsHandleCreated)
            {
                return;
            }

            if (!fastResizeRenderingActive)
            {
                fastResizeRenderingActive = true;
                UiTheme.BeginFastResizeRendering();
            }

            resizeSettleTimer.Stop();
            resizeSettleTimer.Start();
        }

        private void EndFastResizeRendering()
        {
            if (!fastResizeRenderingActive)
            {
                return;
            }

            fastResizeRenderingActive = false;
            UiTheme.EndFastResizeRendering();
            if (!IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(
                        (MethodInvoker)delegate
                        {
                            if (IsDisposed)
                            {
                                return;
                            }

                            Invalidate(true);
                        });
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    internal static class AppIcon
    {
        private static readonly object syncRoot = new object();
        private static Icon cachedIcon;

        public static Icon Load()
        {
            lock (syncRoot)
            {
                if (cachedIcon == null)
                {
                    cachedIcon = LoadCore();
                }

                return (Icon)cachedIcon.Clone();
            }
        }

        private static Icon LoadCore()
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "speaker.ico");
            if (File.Exists(iconPath))
            {
                try
                {
                    using (Icon icon = new Icon(iconPath))
                    {
                        return (Icon)icon.Clone();
                    }
                }
                catch
                {
                }
            }

            try
            {
                Icon associated = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (associated != null)
                {
                    return associated;
                }
            }
            catch
            {
            }

            return SystemIcons.Application;
        }
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AudioOutputSwitcher";
        private const string ScheduledTaskName = "AudioOutputSwitcher";
        private const int SchtasksTimeoutMilliseconds = 10000;

        public static bool IsEnabled()
        {
            return HasRegistryRunEntry() || HasScheduledTask();
        }

        public static void SetEnabled(bool enabled, bool runAsAdministrator)
        {
            bool requiresElevation = runAsAdministrator || HasScheduledTask();
            if (requiresElevation && !ElevationManager.IsProcessElevated())
            {
                string error;
                if (ElevationManager.TryRunElevatedStartupUpdate(enabled, runAsAdministrator, out error))
                {
                    return;
                }

                throw new InvalidOperationException(error ?? "Could not update the Windows startup task.");
            }

            ApplyEnabledState(enabled, runAsAdministrator);
        }

        public static void TryMigrateEnabledMode(bool runAsAdministrator)
        {
            if (!IsEnabled())
            {
                return;
            }

            bool hasRegistry = HasRegistryRunEntry();
            bool hasTask = HasScheduledTask();
            if (runAsAdministrator)
            {
                if (hasTask && ElevationManager.IsProcessElevated() && ScheduledTaskNeedsRepair())
                {
                    CreateScheduledTask();
                    SetRegistryRunEntry(false);
                    return;
                }

                if (hasRegistry && !hasTask && ElevationManager.IsProcessElevated())
                {
                    ApplyEnabledState(true, true);
                }
                return;
            }

            if (hasTask && ElevationManager.IsProcessElevated())
            {
                ApplyEnabledState(true, false);
            }
        }

        public static string GetStartupExecutablePath()
        {
            string official = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AudioOutputSwitcher.exe");
            if (File.Exists(official))
            {
                return official;
            }
            return Application.ExecutablePath;
        }

        internal static void ApplyEnabledState(bool enabled, bool runAsAdministrator)
        {
            if (!enabled)
            {
                RemoveScheduledTask();
                SetRegistryRunEntry(false);
                return;
            }

            if (runAsAdministrator)
            {
                CreateScheduledTask();
                SetRegistryRunEntry(false);
                return;
            }

            RemoveScheduledTask();
            SetRegistryRunEntry(true);
        }

        private static bool HasRegistryRunEntry()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }

                string value = key.GetValue(ValueName) as string;
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }

                string target = GetStartupExecutablePath();
                string commandPath = ExtractCommandPath(value);
                if (string.IsNullOrEmpty(commandPath))
                {
                    return true;
                }

                return string.Equals(commandPath, target, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void SetRegistryRunEntry(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Could not open the Windows startup registry key.");
                }

                if (enabled)
                {
                    key.SetValue(ValueName, Quote(GetStartupExecutablePath()), RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }

        private static bool HasScheduledTask()
        {
            string output;
            if (!TryGetScheduledTaskXml(out output))
            {
                return false;
            }

            string target = GetStartupExecutablePath();
            return output.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void CreateScheduledTask()
        {
            string taskFile = Path.Combine(Path.GetTempPath(), "AudioOutputSwitcher-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(taskFile, BuildScheduledTaskXml(), Encoding.Unicode);

                string output;
                string command = "/Create /TN " + Quote(ScheduledTaskName)
                    + " /XML " + Quote(taskFile)
                    + " /F";
                if (!RunSchtasks(command, false, out output))
                {
                    throw new InvalidOperationException(BuildSchtasksError("Could not create the elevated startup task.", output));
                }
            }
            finally
            {
                try
                {
                    File.Delete(taskFile);
                }
                catch
                {
                }
            }
        }

        private static bool TryGetScheduledTaskXml(out string output)
        {
            return RunSchtasks("/Query /TN " + Quote(ScheduledTaskName) + " /XML", true, out output);
        }

        private static bool ScheduledTaskNeedsRepair()
        {
            string xml;
            if (!TryGetScheduledTaskXml(out xml))
            {
                return false;
            }

            return xml.IndexOf("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.OrdinalIgnoreCase) < 0
                || xml.IndexOf("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase) < 0
                || xml.IndexOf("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", StringComparison.OrdinalIgnoreCase) < 0
                || xml.IndexOf("<StartWhenAvailable>true</StartWhenAvailable>", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string BuildScheduledTaskXml()
        {
            string userId;
            string author;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                userId = identity.User == null ? identity.Name : identity.User.Value;
                author = identity.Name;
            }
            string executable = GetStartupExecutablePath();
            string workingDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string startBoundary = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss");

            StringBuilder xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            xml.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            xml.AppendLine("  <RegistrationInfo>");
            xml.AppendLine("    <Author>" + XmlEscape(author) + "</Author>");
            xml.AppendLine("    <Description>Starts Audio Output Switcher when the user signs in.</Description>");
            xml.AppendLine("  </RegistrationInfo>");
            xml.AppendLine("  <Triggers>");
            xml.AppendLine("    <LogonTrigger>");
            xml.AppendLine("      <StartBoundary>" + startBoundary + "</StartBoundary>");
            xml.AppendLine("      <Enabled>true</Enabled>");
            xml.AppendLine("      <UserId>" + XmlEscape(userId) + "</UserId>");
            xml.AppendLine("    </LogonTrigger>");
            xml.AppendLine("  </Triggers>");
            xml.AppendLine("  <Principals>");
            xml.AppendLine("    <Principal id=\"Author\">");
            xml.AppendLine("      <UserId>" + XmlEscape(userId) + "</UserId>");
            xml.AppendLine("      <LogonType>InteractiveToken</LogonType>");
            xml.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            xml.AppendLine("    </Principal>");
            xml.AppendLine("  </Principals>");
            xml.AppendLine("  <Settings>");
            xml.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            xml.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            xml.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            xml.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            xml.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            xml.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            xml.AppendLine("    <IdleSettings>");
            xml.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            xml.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            xml.AppendLine("    </IdleSettings>");
            xml.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            xml.AppendLine("    <Enabled>true</Enabled>");
            xml.AppendLine("    <Hidden>false</Hidden>");
            xml.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            xml.AppendLine("    <WakeToRun>false</WakeToRun>");
            xml.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            xml.AppendLine("    <Priority>7</Priority>");
            xml.AppendLine("  </Settings>");
            xml.AppendLine("  <Actions Context=\"Author\">");
            xml.AppendLine("    <Exec>");
            xml.AppendLine("      <Command>" + XmlEscape(executable) + "</Command>");
            xml.AppendLine("      <WorkingDirectory>" + XmlEscape(workingDirectory) + "</WorkingDirectory>");
            xml.AppendLine("    </Exec>");
            xml.AppendLine("  </Actions>");
            xml.AppendLine("</Task>");
            return xml.ToString();
        }

        private static string XmlEscape(string value)
        {
            return System.Security.SecurityElement.Escape(value ?? string.Empty);
        }

        private static void RemoveScheduledTask()
        {
            string output;
            RunSchtasks("/Delete /TN " + Quote(ScheduledTaskName) + " /F", true, out output);
        }

        private static bool RunSchtasks(string arguments, bool ignoreFailure, out string output)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
                startInfo.Arguments = arguments;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                StringBuilder capturedOutput = new StringBuilder();
                object outputSync = new object();
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        output = "The Windows Task Scheduler command did not start.";
                        return false;
                    }

                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        AppendProcessOutput(capturedOutput, outputSync, e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        AppendProcessOutput(capturedOutput, outputSync, e.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(SchtasksTimeoutMilliseconds))
                    {
                        TryTerminateProcess(process, "Task Scheduler command");
                        output = "The Windows Task Scheduler command timed out after 10 seconds.";
                        if (!ignoreFailure)
                        {
                            CrashLogger.Write("Task Scheduler command timed out", new TimeoutException(output));
                        }
                        return false;
                    }

                    process.WaitForExit();
                    lock (outputSync)
                    {
                        output = capturedOutput.ToString().Trim();
                    }

                    if (process.ExitCode == 0)
                    {
                        return true;
                    }

                    if (!ignoreFailure)
                    {
                        CrashLogger.Write("Task Scheduler command failed", new InvalidOperationException(BuildSchtasksError("schtasks.exe returned exit code " + process.ExitCode.ToString() + ".", output)));
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                if (!ignoreFailure)
                {
                    CrashLogger.Write("Task Scheduler command failed", ex);
                }
                return false;
            }
        }

        private static void AppendProcessOutput(StringBuilder output, object syncRoot, string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            lock (syncRoot)
            {
                output.AppendLine(line);
            }
        }

        private static void TryTerminateProcess(Process process, string operation)
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex)
            {
                CrashLogger.Write(operation + " could not be terminated after timeout", ex);
            }
        }

        private static string BuildSchtasksError(string prefix, string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return prefix;
            }

            return prefix + " " + output;
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        private static string ExtractCommandPath(string command)
        {
            command = command.Trim();
            if (command.Length == 0)
            {
                return string.Empty;
            }

            if (command[0] == '"')
            {
                int end = command.IndexOf('"', 1);
                if (end > 1)
                {
                    return command.Substring(1, end - 1);
                }
            }

            int exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                return command.Substring(0, exeIndex + 4).Trim();
            }

            int spaceIndex = command.IndexOf(' ');
            if (spaceIndex > 0)
            {
                return command.Substring(0, spaceIndex).Trim();
            }

            return command;
        }
    }

    internal static class ElevationManager
    {
        private const int ElevatedHelperTimeoutMilliseconds = 30000;

        public static bool IsProcessElevated()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static bool TryRelaunchElevated()
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = StartupManager.GetStartupExecutablePath();
                startInfo.Arguments = "--deferred-launch";
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                startInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                Process.Start(startInfo);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryRunElevatedStartupUpdate(bool enabled, bool runAsAdministrator, out string error)
        {
            string action = enabled ? "enable" : "disable";
            string mode = runAsAdministrator ? "highest" : "standard";
            return TryRunElevatedCommand("--startup-sync " + action + " " + mode, out error);
        }

        private static bool TryRunElevatedCommand(string arguments, out string error)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = StartupManager.GetStartupExecutablePath();
                startInfo.Arguments = arguments;
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                startInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "The elevated helper process did not start.";
                        return false;
                    }

                    if (!process.WaitForExit(ElevatedHelperTimeoutMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception ex)
                        {
                            CrashLogger.Write("Elevated startup helper could not be terminated after timeout", ex);
                        }

                        error = "The elevated startup update timed out after 30 seconds. Reopen the settings page to verify the current startup state.";
                        return false;
                    }
                    if (process.ExitCode == 0)
                    {
                        error = null;
                        return true;
                    }

                    error = "The elevated startup update did not complete successfully.";
                    return false;
                }
            }
            catch (Win32Exception ex)
            {
                if ((uint)ex.NativeErrorCode == 1223)
                {
                    error = "Administrator permission was cancelled, so the startup change was not applied.";
                    return false;
                }

                error = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    internal sealed class SwitchToastForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private readonly FormsTimer holdTimer;
        private readonly FormsTimer fadeTimer;
        private readonly Font captionFont;
        private readonly Font deviceFont;
        private readonly StringFormat textFormat;
        private string deviceName = string.Empty;
        private double targetOpacity;
        private double currentOpacity;

        public SwitchToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = UiTheme.AppBackground;
            DoubleBuffered = true;
            Size = new Size(360, 64);

            captionFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            deviceFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            textFormat = new StringFormat();
            textFormat.Alignment = StringAlignment.Near;
            textFormat.LineAlignment = StringAlignment.Center;
            textFormat.Trimming = StringTrimming.EllipsisCharacter;
            textFormat.FormatFlags = StringFormatFlags.NoWrap;

            holdTimer = new FormsTimer();
            holdTimer.Interval = 1650;
            holdTimer.Tick += delegate
            {
                holdTimer.Stop();
                targetOpacity = 0;
                fadeTimer.Start();
            };

            fadeTimer = new FormsTimer();
            fadeTimer.Interval = 16;
            fadeTimer.Tick += delegate
            {
                double step = targetOpacity > currentOpacity ? 0.10 : -0.08;
                double next = currentOpacity + step;

                if (step > 0 && next >= targetOpacity)
                {
                    currentOpacity = targetOpacity;
                    RenderLayered();
                    fadeTimer.Stop();
                }
                else if (step < 0 && next <= targetOpacity)
                {
                    currentOpacity = targetOpacity;
                    RenderLayered();
                    fadeTimer.Stop();
                    if (currentOpacity <= 0)
                    {
                        Hide();
                    }
                }
                else
                {
                    currentOpacity = next;
                    RenderLayered();
                }
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void ShowDevice(string name)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ShowDevice), name);
                return;
            }

            deviceName = string.IsNullOrEmpty(name) ? "Unknown output" : name;
            ResizeToContent();
            PositionNearTaskbar();

            holdTimer.Stop();
            targetOpacity = 0.96;
            if (!Visible)
            {
                currentOpacity = 0;
                Show();
            }

            RenderLayered();
            EnsureTopMostToast();
            fadeTimer.Start();
            holdTimer.Start();
        }

        public void HideImmediately()
        {
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)delegate { HideImmediately(); });
                return;
            }

            holdTimer.Stop();
            fadeTimer.Stop();
            currentOpacity = 0;
            targetOpacity = 0;
            if (Visible)
            {
                Hide();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

        private void RenderToast(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.Default;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            Rectangle shadowRect = new Rectangle(6, 8, Width - 12, Height - 10);
            using (GraphicsPath shadowPath = RoundedRectangle(shadowRect, 20))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            {
                graphics.FillPath(shadowBrush, shadowPath);
            }

            Rectangle cardRect = new Rectangle(4, 4, Width - 8, Height - 4);
            using (GraphicsPath cardPath = RoundedRectangle(cardRect, 18))
            using (SolidBrush cardBrush = new SolidBrush(Color.FromArgb(238, 24, 27, 33)))
            using (Pen borderPen = new Pen(Color.FromArgb(38, 255, 255, 255), 1))
            {
                borderPen.Alignment = PenAlignment.Inset;
                graphics.FillPath(cardBrush, cardPath);
                graphics.DrawPath(borderPen, cardPath);
            }

            const int iconSize = 30;
            int iconTop = cardRect.Top + ((cardRect.Height - iconSize) / 2);
            DrawSpeakerMark(graphics, new Rectangle(22, iconTop, iconSize, iconSize));

            int captionHeight = Math.Max(12, (int)Math.Ceiling(captionFont.GetHeight(graphics)));
            int deviceHeight = Math.Max(18, (int)Math.Ceiling(deviceFont.GetHeight(graphics)));
            int gap = 2;
            int contentHeight = captionHeight + deviceHeight + gap;
            int contentTop = cardRect.Top + ((cardRect.Height - contentHeight) / 2);
            Rectangle captionRect = new Rectangle(64, contentTop, Width - 88, captionHeight + 2);
            Rectangle deviceRect = new Rectangle(64, contentTop + captionHeight + gap, Width - 88, deviceHeight + 2);
            using (SolidBrush captionBrush = new SolidBrush(UiTheme.MutedText))
            using (SolidBrush deviceBrush = new SolidBrush(UiTheme.Text))
            {
                graphics.DrawString("Output switched", captionFont, captionBrush, captionRect, textFormat);
                graphics.DrawString(deviceName, deviceFont, deviceBrush, deviceRect, textFormat);
            }
        }

        private void RenderLayered()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0)
            {
                return;
            }

            using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                RenderToast(graphics);

                IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
                IntPtr memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
                IntPtr bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                IntPtr oldBitmap = NativeMethods.SelectObject(memoryDc, bitmapHandle);

                try
                {
                    NativeMethods.POINT top = new NativeMethods.POINT { x = Left, y = Top };
                    NativeMethods.SIZE size = new NativeMethods.SIZE { cx = Width, cy = Height };
                    NativeMethods.POINT source = new NativeMethods.POINT { x = 0, y = 0 };
                    NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION
                    {
                        BlendOp = NativeMethods.AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(currentOpacity * 255))),
                        AlphaFormat = NativeMethods.AC_SRC_ALPHA
                    };

                    NativeMethods.UpdateLayeredWindow(
                        Handle,
                        screenDc,
                        ref top,
                        ref size,
                        memoryDc,
                        ref source,
                        0,
                        ref blend,
                        NativeMethods.ULW_ALPHA);
                }
                finally
                {
                    NativeMethods.SelectObject(memoryDc, oldBitmap);
                    NativeMethods.DeleteObject(bitmapHandle);
                    NativeMethods.DeleteDC(memoryDc);
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        private void EnsureTopMostToast()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                Left,
                Top,
                Width,
                Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = new IntPtr(HTTRANSPARENT);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                holdTimer.Dispose();
                fadeTimer.Dispose();
                captionFont.Dispose();
                deviceFont.Dispose();
                textFormat.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ResizeToContent()
        {
            Size textSize = TextRenderer.MeasureText(deviceName, deviceFont);
            int width = Math.Max(300, Math.Min(500, textSize.Width + 108));
            Size = new Size(width, 64);
            Region = null;
        }

        private void PositionNearTaskbar()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;
            int x = area.Right - Width - 18;
            int y = area.Bottom - Height - 14;
            Location = new Point(Math.Max(area.Left + 12, x), Math.Max(area.Top + 12, y));
        }

        private static void DrawSpeakerMark(Graphics graphics, Rectangle bounds)
        {
            using (SolidBrush diskBrush = new SolidBrush(UiTheme.Accent))
            {
                graphics.FillEllipse(diskBrush, bounds);
            }

            using (GraphicsPath speaker = new GraphicsPath())
            {
                speaker.AddRectangle(new Rectangle(bounds.Left + 8, bounds.Top + 11, 6, 8));
                speaker.AddPolygon(new Point[]
                {
                    new Point(bounds.Left + 14, bounds.Top + 11),
                    new Point(bounds.Left + 21, bounds.Top + 6),
                    new Point(bounds.Left + 21, bounds.Top + 24),
                    new Point(bounds.Left + 14, bounds.Top + 19)
                });
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    graphics.FillPath(brush, speaker);
                }
            }

            using (Pen wavePen = new Pen(Color.FromArgb(230, 255, 255, 255), 2))
            {
                wavePen.StartCap = LineCap.Round;
                wavePen.EndCap = LineCap.Round;
                graphics.DrawArc(wavePen, bounds.Left + 18, bounds.Top + 9, 8, 12, -45, 90);
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal static class AudioManager
    {
        private const int DEVICE_STATE_ACTIVE = 0x00000001;
        private const int STGM_READ = 0x00000000;
        private static readonly ERole[] PlaybackRoles = new ERole[]
        {
            ERole.eConsole,
            ERole.eMultimedia,
            ERole.eCommunications
        };
        private static readonly object cacheSync = new object();
        private static List<AudioDeviceInfo> cachedRenderDevices;
        private static DateTime cachedRenderDevicesUtc = DateTime.MinValue;
        private static readonly TimeSpan RenderDevicesCacheDuration = TimeSpan.FromSeconds(2);

        public static List<AudioDeviceInfo> GetRenderDevices(bool forceRefresh = false)
        {
            lock (cacheSync)
            {
                if (!forceRefresh && cachedRenderDevices != null && (DateTime.UtcNow - cachedRenderDevicesUtc) < RenderDevicesCacheDuration)
                {
                    return CloneDeviceList(cachedRenderDevices);
                }
            }

            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            IMMDevice defaultDevice = null;
            string defaultId = null;
            List<AudioDeviceInfo> result = new List<AudioDeviceInfo>();

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out defaultDevice);
                if (hr >= 0 && defaultDevice != null)
                {
                    defaultId = GetDeviceId(defaultDevice);
                }

                CheckHr(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out collection));

                int count;
                CheckHr(collection.GetCount(out count));

                for (int i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        CheckHr(collection.Item(i, out device));
                        string id = GetDeviceId(device);
                        string name = GetFriendlyName(device);
                        result.Add(new AudioDeviceInfo
                        {
                            Id = id,
                            Name = string.IsNullOrEmpty(name) ? id : name,
                            IsDefault = defaultId != null && string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    finally
                    {
                        ReleaseCom(device);
                    }
                }

                lock (cacheSync)
                {
                    cachedRenderDevices = CloneDeviceList(result);
                    cachedRenderDevicesUtc = DateTime.UtcNow;
                }

                return result;
            }
            finally
            {
                ReleaseCom(defaultDevice);
                ReleaseCom(collection);
                ReleaseCom(enumerator);
            }
        }

        public static void SetDefaultRenderDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException("An audio device ID is required.", "deviceId");
            }

            InvalidateRenderDevicesCache();
            List<string> readErrors = new List<string>();
            Dictionary<ERole, string> previousRoleIds = ReadDefaultRoleIds(readErrors);
            if (previousRoleIds.Count != PlaybackRoles.Length)
            {
                throw new InvalidOperationException(
                    "Could not read the current Windows audio role state before switching. "
                    + JoinDiagnostics(readErrors));
            }

            List<string> diagnostics = new List<string>();
            TrySetRolesWithPolicyConfig(deviceId, PlaybackRoles, diagnostics);

            Dictionary<ERole, string> currentRoleIds = ReadDefaultRoleIds(diagnostics);
            List<ERole> missingRoles = FindRolesNotUsingDevice(currentRoleIds, deviceId);
            if (missingRoles.Count > 0)
            {
                TrySetRolesWithVistaPolicyConfig(deviceId, missingRoles, diagnostics);
                currentRoleIds = ReadDefaultRoleIds(diagnostics);
                missingRoles = FindRolesNotUsingDevice(currentRoleIds, deviceId);
            }

            if (missingRoles.Count == 0)
            {
                if (diagnostics.Count > 0)
                {
                    CrashLogger.Write(
                        "Primary audio policy interface required fallback",
                        new InvalidOperationException(JoinDiagnostics(diagnostics)));
                }
                InvalidateRenderDevicesCache();
                return;
            }

            List<string> rollbackDiagnostics = new List<string>();
            bool rollbackSucceeded = RestoreDefaultRoleIds(previousRoleIds, rollbackDiagnostics);
            string message = "Windows did not apply the selected device to every playback role. Missing roles: "
                + JoinRoleNames(missingRoles)
                + ". Policy diagnostics: " + JoinDiagnostics(diagnostics)
                + ". Rollback: " + (rollbackSucceeded ? "completed." : "incomplete: " + JoinDiagnostics(rollbackDiagnostics));

            InvalidateRenderDevicesCache();
            InvalidOperationException failure = new InvalidOperationException(message);
            CrashLogger.Write("Audio endpoint switch failed", failure);
            throw failure;
        }

        private static Dictionary<ERole, string> ReadDefaultRoleIds(List<string> diagnostics)
        {
            Dictionary<ERole, string> result = new Dictionary<ERole, string>();
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                for (int i = 0; i < PlaybackRoles.Length; i++)
                {
                    ERole role = PlaybackRoles[i];
                    IMMDevice device = null;
                    try
                    {
                        int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out device);
                        if (hr < 0 || device == null)
                        {
                            diagnostics.Add("Read " + GetRoleName(role) + ": " + DescribeHResult(hr));
                            continue;
                        }

                        result[role] = GetDeviceId(device);
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add("Read " + GetRoleName(role) + ": " + ex.Message);
                    }
                    finally
                    {
                        ReleaseCom(device);
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add("Create audio endpoint enumerator: " + ex.Message);
            }
            finally
            {
                ReleaseCom(enumerator);
            }

            return result;
        }

        public static void InvalidateRenderDevicesCache()
        {
            lock (cacheSync)
            {
                cachedRenderDevices = null;
                cachedRenderDevicesUtc = DateTime.MinValue;
            }
        }

        private static List<AudioDeviceInfo> CloneDeviceList(List<AudioDeviceInfo> source)
        {
            List<AudioDeviceInfo> result = new List<AudioDeviceInfo>();
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                AudioDeviceInfo item = source[i];
                result.Add(new AudioDeviceInfo
                {
                    Id = item.Id,
                    Name = item.Name,
                    IsDefault = item.IsDefault
                });
            }

            return result;
        }

        private static void TrySetRolesWithPolicyConfig(string deviceId, IList<ERole> roles, List<string> diagnostics)
        {
            object policy = null;
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"));
                policy = Activator.CreateInstance(type);
                IPolicyConfig config = (IPolicyConfig)policy;
                for (int i = 0; i < roles.Count; i++)
                {
                    ERole role = roles[i];
                    int hr = config.SetDefaultEndpoint(deviceId, role);
                    if (hr < 0)
                    {
                        diagnostics.Add("PolicyConfig " + GetRoleName(role) + ": " + DescribeHResult(hr));
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add("PolicyConfig unavailable: " + ex.Message);
            }
            finally
            {
                ReleaseCom(policy);
            }
        }

        private static void TrySetRolesWithVistaPolicyConfig(string deviceId, IList<ERole> roles, List<string> diagnostics)
        {
            object policy = null;
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid("294935CE-F637-4E7C-A41B-AB255460B862"));
                policy = Activator.CreateInstance(type);
                IPolicyConfigVista config = (IPolicyConfigVista)policy;
                for (int i = 0; i < roles.Count; i++)
                {
                    ERole role = roles[i];
                    int hr = config.SetDefaultEndpoint(deviceId, role);
                    if (hr < 0)
                    {
                        diagnostics.Add("VistaPolicyConfig " + GetRoleName(role) + ": " + DescribeHResult(hr));
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add("VistaPolicyConfig unavailable: " + ex.Message);
            }
            finally
            {
                ReleaseCom(policy);
            }
        }

        private static bool RestoreDefaultRoleIds(Dictionary<ERole, string> previousRoleIds, List<string> diagnostics)
        {
            object policy = null;
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"));
                policy = Activator.CreateInstance(type);
                IPolicyConfig config = (IPolicyConfig)policy;
                for (int i = 0; i < PlaybackRoles.Length; i++)
                {
                    ERole role = PlaybackRoles[i];
                    string previousId;
                    if (!previousRoleIds.TryGetValue(role, out previousId))
                    {
                        continue;
                    }

                    int hr = config.SetDefaultEndpoint(previousId, role);
                    if (hr < 0)
                    {
                        diagnostics.Add("Rollback PolicyConfig " + GetRoleName(role) + ": " + DescribeHResult(hr));
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add("Rollback PolicyConfig unavailable: " + ex.Message);
            }
            finally
            {
                ReleaseCom(policy);
            }

            Dictionary<ERole, string> currentRoleIds = ReadDefaultRoleIds(diagnostics);
            List<ERole> missingRoles = FindRolesNotMatching(currentRoleIds, previousRoleIds);
            if (missingRoles.Count == 0)
            {
                return true;
            }

            policy = null;
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid("294935CE-F637-4E7C-A41B-AB255460B862"));
                policy = Activator.CreateInstance(type);
                IPolicyConfigVista config = (IPolicyConfigVista)policy;
                for (int i = 0; i < missingRoles.Count; i++)
                {
                    ERole role = missingRoles[i];
                    string previousId;
                    if (!previousRoleIds.TryGetValue(role, out previousId))
                    {
                        continue;
                    }

                    int hr = config.SetDefaultEndpoint(previousId, role);
                    if (hr < 0)
                    {
                        diagnostics.Add("Rollback VistaPolicyConfig " + GetRoleName(role) + ": " + DescribeHResult(hr));
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add("Rollback VistaPolicyConfig unavailable: " + ex.Message);
            }
            finally
            {
                ReleaseCom(policy);
            }

            currentRoleIds = ReadDefaultRoleIds(diagnostics);
            return FindRolesNotMatching(currentRoleIds, previousRoleIds).Count == 0;
        }

        private static List<ERole> FindRolesNotUsingDevice(Dictionary<ERole, string> currentRoleIds, string deviceId)
        {
            List<ERole> result = new List<ERole>();
            for (int i = 0; i < PlaybackRoles.Length; i++)
            {
                ERole role = PlaybackRoles[i];
                string currentId;
                if (!currentRoleIds.TryGetValue(role, out currentId)
                    || !string.Equals(currentId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(role);
                }
            }
            return result;
        }

        private static List<ERole> FindRolesNotMatching(
            Dictionary<ERole, string> currentRoleIds,
            Dictionary<ERole, string> expectedRoleIds)
        {
            List<ERole> result = new List<ERole>();
            for (int i = 0; i < PlaybackRoles.Length; i++)
            {
                ERole role = PlaybackRoles[i];
                string currentId;
                string expectedId;
                if (!currentRoleIds.TryGetValue(role, out currentId)
                    || !expectedRoleIds.TryGetValue(role, out expectedId)
                    || !string.Equals(currentId, expectedId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(role);
                }
            }
            return result;
        }

        private static string JoinRoleNames(IList<ERole> roles)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < roles.Count; i++)
            {
                names.Add(GetRoleName(roles[i]));
            }
            return string.Join(", ", names.ToArray());
        }

        private static string GetRoleName(ERole role)
        {
            if (role == ERole.eConsole)
            {
                return "Console";
            }
            if (role == ERole.eMultimedia)
            {
                return "Multimedia";
            }
            return "Communications";
        }

        private static string DescribeHResult(int hr)
        {
            if (hr >= 0)
            {
                return "success";
            }

            Exception error = Marshal.GetExceptionForHR(hr);
            string message = error == null ? "Unknown COM error" : error.Message;
            return message + " (0x" + hr.ToString("X8") + ")";
        }

        private static string JoinDiagnostics(List<string> diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0)
            {
                return "No additional diagnostics were reported.";
            }
            return string.Join(" | ", diagnostics.ToArray());
        }

        private static string GetDeviceId(IMMDevice device)
        {
            IntPtr idPtr = IntPtr.Zero;
            try
            {
                CheckHr(device.GetId(out idPtr));
                return Marshal.PtrToStringUni(idPtr);
            }
            finally
            {
                if (idPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(idPtr);
                }
            }
        }

        private static string GetFriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            PropVariant value = new PropVariant();
            try
            {
                CheckHr(device.OpenPropertyStore(STGM_READ, out store));
                PROPERTYKEY key = PropertyKeys.PKEY_Device_FriendlyName;
                CheckHr(store.GetValue(ref key, out value));
                return value.GetString();
            }
            finally
            {
                if (value.VariantType != 0)
                {
                    NativeMethods.PropVariantClear(ref value);
                }
                ReleaseCom(store);
            }
        }

        private static void CheckHr(int hr)
        {
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        private static void ReleaseCom(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
    }

    internal sealed class AppSettings
    {
        public bool ShowBalloon = true;
        public bool RunAsAdministrator = false;
        public readonly List<HotkeyBinding> Hotkeys = new List<HotkeyBinding>();
        private readonly List<string> preservedSettings = new List<string>();

        public static AppSettings Load(string path)
        {
            EnsureExists(path);

            AppSettings settings = new AppSettings();
            string[] lines = ReadConfigLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("["))
                {
                    continue;
                }

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0)
                {
                    CrashLogger.Write("Ignored malformed configuration line " + (i + 1).ToString());
                    continue;
                }

                string key = line.Substring(0, equalsIndex).Trim();
                string value = line.Substring(equalsIndex + 1).Trim();
                if (key.Equals("ShowBalloon", StringComparison.OrdinalIgnoreCase))
                {
                    bool parsed;
                    if (TryParseBool(value, out parsed))
                    {
                        settings.ShowBalloon = parsed;
                    }
                    else
                    {
                        CrashLogger.Write("Ignored invalid boolean on configuration line " + (i + 1).ToString());
                    }
                    continue;
                }
                if (key.Equals("RunAsAdministrator", StringComparison.OrdinalIgnoreCase))
                {
                    bool parsed;
                    if (TryParseBool(value, out parsed))
                    {
                        settings.RunAsAdministrator = parsed;
                    }
                    else
                    {
                        CrashLogger.Write("Ignored invalid boolean on configuration line " + (i + 1).ToString());
                    }
                    continue;
                }

                if (value.Length == 0)
                {
                    HotkeyAction emptyAction;
                    if (HotkeyAction.TryParse(key, out emptyAction))
                    {
                        CrashLogger.Write("Ignored empty hotkey on configuration line " + (i + 1).ToString());
                    }
                    else
                    {
                        settings.preservedSettings.Add(raw);
                    }
                    continue;
                }

                HotkeyAction action;
                HotkeyDefinition hotkey;
                if (HotkeyAction.TryParse(key, out action) && HotkeyDefinition.TryParse(value, out hotkey))
                {
                    settings.Hotkeys.Add(new HotkeyBinding
                    {
                        SourceName = key,
                        Action = action,
                        Hotkey = hotkey
                    });
                    continue;
                }

                if (action != null)
                {
                    CrashLogger.Write("Ignored invalid hotkey on configuration line " + (i + 1).ToString());
                    continue;
                }

                settings.preservedSettings.Add(raw);
            }

            return settings;
        }

        public static void EnsureExists(string path)
        {
            if (File.Exists(path))
            {
                return;
            }

            try
            {
                WriteAtomically(path, DefaultConfigText());
            }
            catch (IOException)
            {
                if (!File.Exists(path))
                {
                    throw;
                }
            }
        }

        public void Save(string path)
        {
            WriteAtomically(path, ToConfigText());
        }

        public void CopyPreservedSettingsTo(AppSettings target)
        {
            if (target == null)
            {
                return;
            }

            target.preservedSettings.AddRange(preservedSettings);
        }

        private string ToConfigText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# AudioOutputSwitcher.ini");
            sb.AppendLine("# Edit in the tray app with Hotkey settings, or edit this file and choose Reload config.");
            sb.AppendLine("# ShowBalloon controls routine tray notifications and switch confirmation toasts. Errors remain visible.");
            sb.AppendLine("# RunAsAdministrator keeps manual launch in admin mode and uses a highest-privilege logon task when Start with Windows is enabled.");
            sb.AppendLine("# Keyboard examples: Ctrl+Alt+F12, Ctrl+Shift+1, Win+Alt+S.");
            sb.AppendLine("# Mouse examples: MouseX1, MouseX2, MouseMiddle, Ctrl+MouseX1, Shift+MouseRight.");
            sb.AppendLine("# Actions: Next, Previous, Device1, Device2, Name:part-of-device-name.");
            sb.AppendLine();
            sb.AppendLine("ShowBalloon=" + (ShowBalloon ? "true" : "false"));
            sb.AppendLine("RunAsAdministrator=" + (RunAsAdministrator ? "true" : "false"));
            for (int i = 0; i < Hotkeys.Count; i++)
            {
                HotkeyBinding binding = Hotkeys[i];
                if (binding.Action == null || binding.Hotkey == null || binding.Hotkey.IsEmpty)
                {
                    continue;
                }

                string sourceName = binding.SourceName;
                if (string.IsNullOrEmpty(sourceName))
                {
                    sourceName = binding.Action.ToSourceName();
                }
                sb.AppendLine(sourceName + "=" + binding.Hotkey.ToConfigString());
            }

            if (preservedSettings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("# Settings preserved from a newer or extended configuration.");
                for (int i = 0; i < preservedSettings.Count; i++)
                {
                    sb.AppendLine(preservedSettings[i]);
                }
            }
            return sb.ToString();
        }

        private static bool TryParseBool(string value, out bool result)
        {
            if (bool.TryParse(value, out result))
            {
                return true;
            }
            if (value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }
            if (value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }

            result = false;
            return false;
        }

        private static string[] ReadConfigLines(string path)
        {
            try
            {
                return File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception primaryException)
            {
                string backupPath = path + ".bak";
                CrashLogger.Write("Primary configuration could not be read", primaryException);
                if (!File.Exists(backupPath))
                {
                    throw;
                }

                try
                {
                    string[] backupLines = File.ReadAllLines(backupPath, Encoding.UTF8);
                    CrashLogger.Write("Loaded configuration from the backup copy");
                    return backupLines;
                }
                catch (Exception backupException)
                {
                    CrashLogger.Write("Backup configuration could not be read", backupException);
                    throw new IOException("The configuration and its backup could not be read.", backupException);
                }
            }
        }

        private static void WriteAtomically(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = path + ".write-" + Guid.NewGuid().ToString("N") + ".tmp";
            string backupPath = path + ".bak";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(contents);
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, backupPath, true);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Write("Configuration save failed", ex);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }

        private static string DefaultConfigText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# AudioOutputSwitcher.ini");
            sb.AppendLine("# Edit this file, then right-click the tray icon and choose Reload config.");
            sb.AppendLine("# ShowBalloon controls routine tray notifications and switch confirmation toasts. Errors remain visible.");
            sb.AppendLine("# RunAsAdministrator keeps manual launch in admin mode and uses a highest-privilege logon task when Start with Windows is enabled.");
            sb.AppendLine("# Hotkey examples: Ctrl+Alt+F12, Ctrl+Shift+1, Win+Alt+S, MouseX1, Ctrl+MouseX1.");
            sb.AppendLine("# Actions:");
            sb.AppendLine("#   Next=Ctrl+Alt+F12");
            sb.AppendLine("#   Previous=Ctrl+Alt+F11");
            sb.AppendLine("#   Device1=Ctrl+Alt+1");
            sb.AppendLine("#   Name:Headphones=Ctrl+Alt+H");
            sb.AppendLine("#   Next=MouseX1");
            sb.AppendLine();
            sb.AppendLine("ShowBalloon=true");
            sb.AppendLine("RunAsAdministrator=false");
            sb.AppendLine("Next=Ctrl+Alt+F12");
            sb.AppendLine("# Previous=Ctrl+Alt+F11");
            sb.AppendLine("# Device1=Ctrl+Alt+1");
            sb.AppendLine("# Device2=Ctrl+Alt+2");
            sb.AppendLine("# Name:Headphones=Ctrl+Alt+H");
            return sb.ToString();
        }
    }

    internal sealed class HotkeyBinding
    {
        public string SourceName;
        public HotkeyAction Action;
        public HotkeyDefinition Hotkey;
    }

    internal enum HotkeyActionKind
    {
        Next,
        Previous,
        DeviceIndex,
        DeviceNameContains
    }

    internal sealed class HotkeyAction
    {
        public HotkeyActionKind Kind;
        public int DeviceIndex;
        public string NameContains;

        public HotkeyAction Clone()
        {
            return new HotkeyAction
            {
                Kind = Kind,
                DeviceIndex = DeviceIndex,
                NameContains = NameContains
            };
        }

        public string ToSourceName()
        {
            if (Kind == HotkeyActionKind.Next)
            {
                return "Next";
            }
            if (Kind == HotkeyActionKind.Previous)
            {
                return "Previous";
            }
            if (Kind == HotkeyActionKind.DeviceIndex)
            {
                return "Device" + (DeviceIndex + 1).ToString();
            }
            if (Kind == HotkeyActionKind.DeviceNameContains)
            {
                return "Name:" + NameContains;
            }
            return "Next";
        }

        public string ToDisplayName()
        {
            if (Kind == HotkeyActionKind.Next)
            {
                return "Next output";
            }
            if (Kind == HotkeyActionKind.Previous)
            {
                return "Previous output";
            }
            if (Kind == HotkeyActionKind.DeviceIndex)
            {
                return "Device " + (DeviceIndex + 1).ToString();
            }
            if (Kind == HotkeyActionKind.DeviceNameContains)
            {
                return "Switch to: " + NameContains;
            }
            return ToSourceName();
        }

        public bool SameAs(HotkeyAction other)
        {
            if (other == null || Kind != other.Kind)
            {
                return false;
            }
            if (Kind == HotkeyActionKind.DeviceIndex)
            {
                return DeviceIndex == other.DeviceIndex;
            }
            if (Kind == HotkeyActionKind.DeviceNameContains)
            {
                return string.Equals(NameContains, other.NameContains, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        public static bool TryParse(string text, out HotkeyAction action)
        {
            action = null;
            string value = text.Trim();
            if (value.Equals("Next", StringComparison.OrdinalIgnoreCase))
            {
                action = new HotkeyAction { Kind = HotkeyActionKind.Next };
                return true;
            }
            if (value.Equals("Previous", StringComparison.OrdinalIgnoreCase) || value.Equals("Prev", StringComparison.OrdinalIgnoreCase))
            {
                action = new HotkeyAction { Kind = HotkeyActionKind.Previous };
                return true;
            }
            if (value.StartsWith("Device", StringComparison.OrdinalIgnoreCase))
            {
                string number = value.Substring("Device".Length).Trim();
                int index;
                if (int.TryParse(number, out index) && index > 0)
                {
                    action = new HotkeyAction { Kind = HotkeyActionKind.DeviceIndex, DeviceIndex = index - 1 };
                    return true;
                }
            }
            if (value.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
            {
                string name = value.Substring("Name:".Length).Trim();
                if (name.Length > 0)
                {
                    action = new HotkeyAction { Kind = HotkeyActionKind.DeviceNameContains, NameContains = name };
                    return true;
                }
            }
            if (value.StartsWith("DeviceName:", StringComparison.OrdinalIgnoreCase))
            {
                string name = value.Substring("DeviceName:".Length).Trim();
                if (name.Length > 0)
                {
                    action = new HotkeyAction { Kind = HotkeyActionKind.DeviceNameContains, NameContains = name };
                    return true;
                }
            }
            return false;
        }
    }

    internal enum MouseInputButton
    {
        Left,
        Right,
        Middle,
        X1,
        X2
    }

    internal sealed class HotkeyDefinition
    {
        public readonly List<Keys> KeyboardKeys = new List<Keys>();
        public readonly List<MouseInputButton> MouseButtons = new List<MouseInputButton>();
        public string DisplayText;

        public bool IsEmpty
        {
            get { return KeyboardKeys.Count == 0 && MouseButtons.Count == 0; }
        }

        public HotkeyDefinition Clone()
        {
            HotkeyDefinition clone = new HotkeyDefinition();
            for (int i = 0; i < KeyboardKeys.Count; i++)
            {
                clone.AddKey(KeyboardKeys[i]);
            }
            for (int i = 0; i < MouseButtons.Count; i++)
            {
                clone.AddMouseButton(MouseButtons[i]);
            }
            clone.DisplayText = ToConfigString();
            return clone;
        }

        public void AddKey(Keys key)
        {
            key = NormalizeKey(key);
            if (key == Keys.None)
            {
                return;
            }
            if (!KeyboardKeys.Contains(key))
            {
                KeyboardKeys.Add(key);
                SortKeys();
            }
        }

        public void RemoveKey(Keys key)
        {
            key = NormalizeKey(key);
            KeyboardKeys.Remove(key);
        }

        public void AddMouseButton(MouseInputButton button)
        {
            if (!MouseButtons.Contains(button))
            {
                MouseButtons.Add(button);
                MouseButtons.Sort();
            }
        }

        public bool Matches(HashSet<Keys> keysDown, HashSet<MouseInputButton> mouseDown)
        {
            if (IsEmpty)
            {
                return false;
            }
            if (keysDown.Count != KeyboardKeys.Count || mouseDown.Count != MouseButtons.Count)
            {
                return false;
            }
            for (int i = 0; i < KeyboardKeys.Count; i++)
            {
                if (!keysDown.Contains(KeyboardKeys[i]))
                {
                    return false;
                }
            }
            for (int i = 0; i < MouseButtons.Count; i++)
            {
                if (!mouseDown.Contains(MouseButtons[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public string ToConfigString()
        {
            List<string> parts = new List<string>();
            AddIfPresent(parts, Keys.ControlKey, "Ctrl");
            AddIfPresent(parts, Keys.Menu, "Alt");
            AddIfPresent(parts, Keys.ShiftKey, "Shift");
            AddIfPresent(parts, Keys.LWin, "Win");

            for (int i = 0; i < KeyboardKeys.Count; i++)
            {
                Keys key = KeyboardKeys[i];
                if (!IsModifierKey(key))
                {
                    parts.Add(KeyToDisplayName(key));
                }
            }

            for (int i = 0; i < MouseButtons.Count; i++)
            {
                parts.Add(MouseButtonToDisplayName(MouseButtons[i]));
            }

            return string.Join("+", parts.ToArray());
        }

        public static bool TryParse(string text, out HotkeyDefinition hotkey)
        {
            hotkey = new HotkeyDefinition();
            string[] parts = text.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                MouseInputButton mouseButton;
                if (TryParseMouseButton(token, out mouseButton))
                {
                    hotkey.AddMouseButton(mouseButton);
                    continue;
                }

                Keys parsed;
                if (!TryParseKey(token, out parsed))
                {
                    hotkey = null;
                    return false;
                }
                hotkey.AddKey(parsed);
            }

            if (hotkey.IsEmpty)
            {
                hotkey = null;
                return false;
            }

            hotkey.DisplayText = hotkey.ToConfigString();
            return true;
        }

        public static bool TryParseMouseButton(string token, out MouseInputButton button)
        {
            button = MouseInputButton.Left;
            string value = token.Trim().Replace(" ", "").Replace("_", "").Replace("-", "");
            if (value.Equals("MouseLeft", StringComparison.OrdinalIgnoreCase) || value.Equals("LeftMouse", StringComparison.OrdinalIgnoreCase) || value.Equals("LButton", StringComparison.OrdinalIgnoreCase))
            {
                button = MouseInputButton.Left;
                return true;
            }
            if (value.Equals("MouseRight", StringComparison.OrdinalIgnoreCase) || value.Equals("RightMouse", StringComparison.OrdinalIgnoreCase) || value.Equals("RButton", StringComparison.OrdinalIgnoreCase))
            {
                button = MouseInputButton.Right;
                return true;
            }
            if (value.Equals("MouseMiddle", StringComparison.OrdinalIgnoreCase) || value.Equals("MiddleMouse", StringComparison.OrdinalIgnoreCase) || value.Equals("MButton", StringComparison.OrdinalIgnoreCase))
            {
                button = MouseInputButton.Middle;
                return true;
            }
            if (value.Equals("MouseX1", StringComparison.OrdinalIgnoreCase) || value.Equals("X1", StringComparison.OrdinalIgnoreCase) || value.Equals("XButton1", StringComparison.OrdinalIgnoreCase))
            {
                button = MouseInputButton.X1;
                return true;
            }
            if (value.Equals("MouseX2", StringComparison.OrdinalIgnoreCase) || value.Equals("X2", StringComparison.OrdinalIgnoreCase) || value.Equals("XButton2", StringComparison.OrdinalIgnoreCase))
            {
                button = MouseInputButton.X2;
                return true;
            }
            return false;
        }

        public static bool TryParseKey(string token, out Keys key)
        {
            key = Keys.None;
            string value = token.Trim().Replace(" ", "");
            if (value.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || value.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                key = Keys.ControlKey;
                return true;
            }
            if (value.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                key = Keys.Menu;
                return true;
            }
            if (value.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                key = Keys.ShiftKey;
                return true;
            }
            if (value.Equals("Win", StringComparison.OrdinalIgnoreCase) || value.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                key = Keys.LWin;
                return true;
            }
            if (value.Length == 1)
            {
                char c = char.ToUpperInvariant(value[0]);
                if (c >= 'A' && c <= 'Z')
                {
                    key = (Keys)((int)Keys.A + (c - 'A'));
                    return true;
                }
                if (c >= '0' && c <= '9')
                {
                    key = (Keys)((int)Keys.D0 + (c - '0'));
                    return true;
                }
            }

            if (value.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && value.Length == 4)
            {
                char c = value[3];
                if (c >= '0' && c <= '9')
                {
                    key = (Keys)((int)Keys.NumPad0 + (c - '0'));
                    return true;
                }
            }

            try
            {
                key = NormalizeKey((Keys)Enum.Parse(typeof(Keys), value, true));
                return key != Keys.None;
            }
            catch
            {
                return false;
            }
        }

        public static Keys NormalizeKey(Keys key)
        {
            if (key == Keys.LControlKey || key == Keys.RControlKey || key == Keys.Control)
            {
                return Keys.ControlKey;
            }
            if (key == Keys.LMenu || key == Keys.RMenu || key == Keys.Alt)
            {
                return Keys.Menu;
            }
            if (key == Keys.LShiftKey || key == Keys.RShiftKey || key == Keys.Shift)
            {
                return Keys.ShiftKey;
            }
            if (key == Keys.RWin)
            {
                return Keys.LWin;
            }
            return key & Keys.KeyCode;
        }

        public static bool IsModifierKey(Keys key)
        {
            key = NormalizeKey(key);
            return key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey || key == Keys.LWin;
        }

        public static string MouseButtonToDisplayName(MouseInputButton button)
        {
            if (button == MouseInputButton.Left)
            {
                return "MouseLeft";
            }
            if (button == MouseInputButton.Right)
            {
                return "MouseRight";
            }
            if (button == MouseInputButton.Middle)
            {
                return "MouseMiddle";
            }
            if (button == MouseInputButton.X1)
            {
                return "MouseX1";
            }
            if (button == MouseInputButton.X2)
            {
                return "MouseX2";
            }
            return "Mouse";
        }

        public static string KeyToDisplayName(Keys key)
        {
            key = NormalizeKey(key);
            if (key == Keys.ControlKey)
            {
                return "Ctrl";
            }
            if (key == Keys.Menu)
            {
                return "Alt";
            }
            if (key == Keys.ShiftKey)
            {
                return "Shift";
            }
            if (key == Keys.LWin)
            {
                return "Win";
            }
            if (key >= Keys.D0 && key <= Keys.D9)
            {
                return ((char)('0' + ((int)key - (int)Keys.D0))).ToString();
            }
            if (key >= Keys.A && key <= Keys.Z)
            {
                return key.ToString();
            }
            return key.ToString();
        }

        private void AddIfPresent(List<string> parts, Keys key, string displayName)
        {
            if (KeyboardKeys.Contains(key))
            {
                parts.Add(displayName);
            }
        }

        private void SortKeys()
        {
            KeyboardKeys.Sort(delegate(Keys left, Keys right)
            {
                return KeySortOrder(left).CompareTo(KeySortOrder(right));
            });
        }

        private static int KeySortOrder(Keys key)
        {
            key = NormalizeKey(key);
            if (key == Keys.ControlKey)
            {
                return 0;
            }
            if (key == Keys.Menu)
            {
                return 1;
            }
            if (key == Keys.ShiftKey)
            {
                return 2;
            }
            if (key == Keys.LWin)
            {
                return 3;
            }
            return 1000 + (int)key;
        }
    }

    internal sealed class HotkeyActionOption
    {
        public string DisplayName;
        public string SourceName;
        public HotkeyAction Action;

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal sealed class ToggleSwitchControl : Control
    {
        private bool isChecked;
        private bool hovered;
        private bool pressed;
        private bool focused;
        private float animationProgress;
        private float animationTarget;
        private readonly FormsTimer animationTimer;
        private Bitmap cachedSurface;
        private Size cachedSurfaceSize;
        private int cachedSurfaceToken;
        public int CornerRadius = 12;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value)
                {
                    return;
                }
                isChecked = value;
                animationTarget = isChecked ? 1f : 0f;
                if (!IsHandleCreated)
                {
                    animationProgress = animationTarget;
                }
                else
                {
                    StartAnimation();
                }
                Invalidate();
                EventHandler handler = CheckedChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        public ToggleSwitchControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);
            Width = 40;
            Height = 24;
            MinimumSize = new Size(40, 24);
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Color.Transparent;
            animationTarget = 0f;
            animationProgress = 0f;
            animationTimer = new FormsTimer();
            animationTimer.Interval = 15;
            animationTimer.Tick += AnimateTick;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter)
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            animationProgress = isChecked ? 1f : 0f;
            animationTarget = animationProgress;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Checked = !Checked;
                e.SuppressKeyPress = true;
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            base.OnMouseEnter(e);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true;
            base.OnMouseDown(e);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            base.OnMouseUp(e);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            focused = true;
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            focused = false;
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color backColor = UiTheme.ResolveParentBackColor(this);
            e.Graphics.Clear(backColor);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int stateToken = UiTheme.HashState(
                Width,
                Height,
                backColor.ToArgb(),
                (int)Math.Round(animationProgress * 1000f),
                hovered ? 1 : 0,
                pressed ? 1 : 0,
                focused ? 1 : 0,
                Enabled ? 1 : 0,
                CornerRadius);
            UiTheme.DrawCachedSupersampled(e.Graphics, Size, backColor, ref cachedSurface, ref cachedSurfaceSize, ref cachedSurfaceToken, stateToken, delegate(Graphics graphics)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.Default;

                RectangleF track = new RectangleF(1f, 2f, Width - 2f, Height - 4f);
                float trackRadius = track.Height / 2f;
                Color offTrack = Enabled ? Color.FromArgb(68, 72, 80) : Color.FromArgb(44, 48, 56);
                Color onTrack = Enabled ? Color.FromArgb(52, 135, 246) : Color.FromArgb(52, 84, 122);
                Color offBorder = Enabled ? Color.FromArgb(96, 101, 112) : Color.FromArgb(62, 68, 78);
                Color onBorder = Enabled ? Color.FromArgb(78, 154, 248) : Color.FromArgb(82, 116, 152);
                Color trackFill = UiTheme.Blend(offTrack, onTrack, animationProgress);
                Color trackBorder = UiTheme.Blend(offBorder, onBorder, animationProgress);
                if (hovered && Enabled)
                {
                    trackFill = UiTheme.Blend(trackFill, Color.FromArgb(96, 160, 255), 0.06f);
                }

                using (GraphicsPath trackPath = UiTheme.CreateRoundedRectangle(track, trackRadius))
                using (SolidBrush trackBrush = new SolidBrush(trackFill))
                using (Pen borderPen = new Pen(trackBorder, 1f))
                {
                    borderPen.Alignment = PenAlignment.Inset;
                    graphics.FillPath(trackBrush, trackPath);
                    graphics.DrawPath(borderPen, trackPath);
                }

                float knobInset = 2f;
                float knobSize = track.Height - (knobInset * 2f);
                float minKnobX = track.Left + knobInset;
                float maxKnobX = track.Right - knobInset - knobSize;
                float knobX = minKnobX + ((maxKnobX - minKnobX) * animationProgress);
                RectangleF knobShadow = new RectangleF(knobX, track.Top + knobInset + 0.8f, knobSize, knobSize);
                using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(44, 0, 0, 0)))
                {
                    graphics.FillEllipse(shadowBrush, knobShadow);
                }

                RectangleF knob = new RectangleF(knobX, track.Top + knobInset, knobSize, knobSize);
                using (LinearGradientBrush knobBrush = new LinearGradientBrush(
                    knob,
                    Color.FromArgb(255, 255, 255, 255),
                    Color.FromArgb(255, 236, 239, 245),
                    LinearGradientMode.Vertical))
                using (Pen knobBorder = new Pen(Color.FromArgb(84, 148, 158, 172), 1f))
                {
                    graphics.FillEllipse(knobBrush, knob);
                    graphics.DrawEllipse(knobBorder, knob);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Dispose();
                UiTheme.DisposeBitmap(ref cachedSurface);
            }
            base.Dispose(disposing);
        }

        private void StartAnimation()
        {
            if (Math.Abs(animationTarget - animationProgress) < 0.001f)
            {
                animationProgress = animationTarget;
                animationTimer.Stop();
                return;
            }

            if (!animationTimer.Enabled)
            {
                animationTimer.Start();
            }
        }

        private void AnimateTick(object sender, EventArgs e)
        {
            float delta = animationTarget - animationProgress;
            if (Math.Abs(delta) <= 0.01f)
            {
                animationProgress = animationTarget;
                animationTimer.Stop();
                Invalidate();
                return;
            }

            animationProgress += delta * 0.38f;
            Invalidate();
        }
    }

    internal sealed class CardPanel : Panel
    {
        public int CornerRadius = 16;
        public Color FillColor = UiTheme.Panel;
        public Color BorderColor = UiTheme.Border;
        private Bitmap cachedSurface;
        private Size cachedSurfaceSize;
        private int cachedSurfaceToken;

        public CardPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color parentColor = UiTheme.ResolveParentBackColor(this);
            e.Graphics.Clear(parentColor);
            int stateToken = UiTheme.HashState(
                Width,
                Height,
                parentColor.ToArgb(),
                FillColor.ToArgb(),
                BorderColor.ToArgb(),
                CornerRadius);
            UiTheme.DrawCachedSupersampled(e.Graphics, Size, parentColor, ref cachedSurface, ref cachedSurfaceSize, ref cachedSurfaceToken, stateToken, delegate(Graphics graphics)
            {
                RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (GraphicsPath path = UiTheme.CreateRoundedRectangle(rect, CornerRadius))
                using (SolidBrush brush = new SolidBrush(FillColor))
                using (Pen borderPen = new Pen(BorderColor, 1f))
                {
                    borderPen.Alignment = PenAlignment.Inset;
                    graphics.FillPath(brush, path);
                    graphics.DrawPath(borderPen, path);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UiTheme.DisposeBitmap(ref cachedSurface);
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ThemedButton : Button
    {
        private bool hovered;
        private bool pressed;
        private bool focused;
        private Bitmap cachedSurface;
        private Size cachedSurfaceSize;
        private int cachedSurfaceToken;

        public bool Primary { get; set; }
        public Color ForegroundColor { get; set; }
        public int CornerRadius = 10;

        public ThemedButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);

            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Margin = new Padding(0);
            Height = 36;
            Cursor = Cursors.Hand;
            Font = UiTheme.UiFontBold;
            ForegroundColor = UiTheme.Text;
            BackColor = UiTheme.Panel;
            UseVisualStyleBackColor = false;
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(UiTheme.ResolveParentBackColor(this));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            base.OnMouseEnter(e);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            pressed = true;
            base.OnMouseDown(mevent);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            pressed = false;
            base.OnMouseUp(mevent);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            focused = true;
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            focused = false;
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color backColor = UiTheme.ResolveParentBackColor(this);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color fillColor = ResolveFillColor();
            Color borderColor = ResolveBorderColor();
            Color textColor = Enabled ? ForegroundColor : UiTheme.MutedText;
            int stateToken = UiTheme.HashState(
                Width,
                Height,
                backColor.ToArgb(),
                fillColor.ToArgb(),
                borderColor.ToArgb(),
                focused ? 1 : 0,
                Enabled ? 1 : 0,
                Primary ? 1 : 0,
                CornerRadius);
            UiTheme.DrawCachedSupersampled(e.Graphics, Size, backColor, ref cachedSurface, ref cachedSurfaceSize, ref cachedSurfaceToken, stateToken, delegate(Graphics graphics)
            {
                RectangleF rect = new RectangleF(1.5f, 1.5f, Width - 3.0f, Height - 3.0f);
                using (GraphicsPath path = UiTheme.CreateRoundedRectangle(rect, CornerRadius))
                using (SolidBrush fillBrush = new SolidBrush(fillColor))
                using (Pen borderPen = new Pen(borderColor, 1f))
                {
                    borderPen.Alignment = PenAlignment.Inset;
                    graphics.FillPath(fillBrush, path);
                    graphics.DrawPath(borderPen, path);
                }

                if (focused)
                {
                    RectangleF focusRect = new RectangleF(4.0f, 4.0f, Width - 8.0f, Height - 8.0f);
                    using (GraphicsPath focusPath = UiTheme.CreateRoundedRectangle(focusRect, Math.Max(2, CornerRadius - 2)))
                    using (Pen focusPen = new Pen(Color.FromArgb(108, 174, 255), 1f))
                    {
                        graphics.DrawPath(focusPen, focusPath);
                    }
                }
            });

            Rectangle textRect = new Rectangle(10, 0, Width - 20, Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textRect,
                textColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UiTheme.DisposeBitmap(ref cachedSurface);
            }
            base.Dispose(disposing);
        }

        private Color ResolveFillColor()
        {
            if (!Enabled)
            {
                return Color.FromArgb(28, 32, 39);
            }

            if (Primary)
            {
                if (pressed)
                {
                    return Color.FromArgb(44, 86, 146);
                }
                if (hovered)
                {
                    return Color.FromArgb(57, 102, 168);
                }
                return Color.FromArgb(49, 92, 154);
            }

            if (pressed)
            {
                return Color.FromArgb(31, 36, 44);
            }
            if (hovered)
            {
                return Color.FromArgb(37, 42, 50);
            }
            return UiTheme.PanelElevated;
        }

        private Color ResolveBorderColor()
        {
            if (!Enabled)
            {
                return UiTheme.Border;
            }

            return Primary ? Color.FromArgb(110, 180, 255) : UiTheme.Border;
        }
    }

    internal sealed class CodexScrollBar : Control
    {
        private int maximum;
        private int viewportSize;
        private int currentValue;
        private bool hovered;
        private bool hoveredThumb;
        private bool dragging;
        private float dragOffset;
        private Bitmap cachedSurface;
        private Size cachedSurfaceSize;
        private int cachedSurfaceToken;

        public event EventHandler ValueChanged;

        public int Value
        {
            get { return currentValue; }
            set
            {
                int clamped = Math.Max(0, Math.Min(maximum, value));
                if (currentValue == clamped)
                {
                    return;
                }

                currentValue = clamped;
                Invalidate();
                EventHandler handler = ValueChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        public CodexScrollBar()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);
            Width = 16;
            MinimumSize = new Size(16, 0);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public void SetMetrics(int maximumScroll, int viewport, int value)
        {
            int nextMaximum = Math.Max(0, maximumScroll);
            int nextViewport = Math.Max(0, viewport);
            int nextValue = Math.Max(0, Math.Min(nextMaximum, value));
            if (maximum == nextMaximum && viewportSize == nextViewport && currentValue == nextValue)
            {
                return;
            }

            maximum = nextMaximum;
            viewportSize = nextViewport;
            currentValue = nextValue;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color backColor = UiTheme.ResolveParentBackColor(this);
            int stateToken = UiTheme.HashState(
                Width,
                Height,
                backColor.ToArgb(),
                maximum,
                viewportSize,
                currentValue,
                hovered ? 1 : 0,
                hoveredThumb ? 1 : 0,
                dragging ? 1 : 0);
            UiTheme.DrawCachedSupersampled(e.Graphics, Size, backColor, ref cachedSurface, ref cachedSurfaceSize, ref cachedSurfaceToken, stateToken, delegate(Graphics graphics)
            {
                RectangleF rail = GetRailRectangle();
                using (GraphicsPath railPath = UiTheme.CreateRoundedRectangle(rail, rail.Width / 2f))
                using (SolidBrush railBrush = new SolidBrush(Color.FromArgb(42, 33, 37, 45)))
                {
                    graphics.FillPath(railBrush, railPath);
                }

                if (maximum <= 0 || viewportSize <= 0)
                {
                    return;
                }

                RectangleF thumb = GetThumbRectangle();
                Color thumbColor = dragging
                    ? Color.FromArgb(132, 148, 160)
                    : hoveredThumb
                        ? Color.FromArgb(114, 126, 140)
                        : hovered
                            ? Color.FromArgb(98, 110, 124)
                            : Color.FromArgb(82, 92, 104);

                using (GraphicsPath thumbPath = UiTheme.CreateRoundedRectangle(thumb, thumb.Width / 2f))
                using (SolidBrush thumbBrush = new SolidBrush(thumbColor))
                {
                    graphics.FillPath(thumbBrush, thumbPath);
                }
            });
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            base.OnMouseEnter(e);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            if (!dragging)
            {
                hoveredThumb = false;
            }
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (maximum <= 0 || viewportSize <= 0)
            {
                return;
            }

            RectangleF thumb = GetThumbRectangle();
            if (thumb.Contains(e.Location))
            {
                dragging = true;
                dragOffset = e.Y - thumb.Top;
                Capture = true;
            }
            else
            {
                int page = Math.Max(48, viewportSize - 24);
                Value += e.Y < thumb.Top ? -page : page;
            }

            hoveredThumb = thumb.Contains(e.Location);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            RectangleF thumb = GetThumbRectangle();
            bool nextHoveredThumb = thumb.Contains(e.Location);
            if (hoveredThumb != nextHoveredThumb)
            {
                hoveredThumb = nextHoveredThumb;
                Invalidate();
            }

            if (!dragging || maximum <= 0)
            {
                return;
            }

            RectangleF rail = GetRailRectangle();
            float thumbHeight = thumb.Height;
            float usableHeight = Math.Max(1f, rail.Height - thumbHeight);
            float top = Math.Max(rail.Top, Math.Min(rail.Bottom - thumbHeight, e.Y - dragOffset));
            float ratio = (top - rail.Top) / usableHeight;
            Value = (int)Math.Round(ratio * maximum);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            Capture = false;
            base.OnMouseUp(e);
            hoveredThumb = GetThumbRectangle().Contains(e.Location);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UiTheme.DisposeBitmap(ref cachedSurface);
            }
            base.Dispose(disposing);
        }

        private RectangleF GetRailRectangle()
        {
            float width = 8f;
            float left = (Width - width) / 2f;
            return new RectangleF(left, 8f, width, Math.Max(24f, Height - 16f));
        }

        private RectangleF GetThumbRectangle()
        {
            RectangleF rail = GetRailRectangle();
            if (maximum <= 0 || viewportSize <= 0)
            {
                return new RectangleF(rail.Left + 1f, rail.Top, rail.Width - 2f, 0f);
            }

            float total = maximum + viewportSize;
            float thumbHeight = Math.Max(36f, (rail.Height * viewportSize) / Math.Max(1f, total));
            thumbHeight = Math.Min(rail.Height, thumbHeight);
            float usableHeight = Math.Max(1f, rail.Height - thumbHeight);
            float top = rail.Top + ((maximum <= 0 ? 0f : (usableHeight * currentValue) / maximum));
            return new RectangleF(rail.Left + 1f, top, rail.Width - 2f, thumbHeight);
        }
    }

    internal sealed class CodexFlowScrollHost : UserControl
    {
        private readonly TableLayoutPanel layout;
        private readonly Panel viewport;
        private readonly CodexScrollBar scrollBar;
        private Color surfaceColor = UiTheme.AppBackground;
        private bool reserveScrollBarGutter = true;
        private int scrollValue;

        public FlowLayoutPanel Content { get; private set; }

        public Color SurfaceColor
        {
            get { return surfaceColor; }
            set
            {
                if (surfaceColor.ToArgb() == value.ToArgb())
                {
                    return;
                }

                surfaceColor = value;
                ApplySurfaceColor();
            }
        }

        public bool ReserveScrollBarGutter
        {
            get { return reserveScrollBarGutter; }
            set
            {
                if (reserveScrollBarGutter == value)
                {
                    return;
                }

                reserveScrollBarGutter = value;
                ApplyScrollBarLayout();
                UpdateContentLayout();
                SyncScrollBar();
            }
        }

        public CodexFlowScrollHost()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);

            BackColor = surfaceColor;

            layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.BackColor = surfaceColor;
            layout.Margin = new Padding(0);
            layout.Padding = new Padding(0);
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            Controls.Add(layout);

            viewport = new Panel();
            viewport.Dock = DockStyle.Fill;
            viewport.Margin = new Padding(0);
            viewport.Padding = new Padding(0);
            viewport.BackColor = surfaceColor;
            layout.Controls.Add(viewport, 0, 0);

            scrollBar = new CodexScrollBar();
            scrollBar.Dock = DockStyle.Fill;
            scrollBar.Margin = new Padding(0);
            scrollBar.ValueChanged += delegate { SetScrollValue(scrollBar.Value); };
            layout.Controls.Add(scrollBar, 1, 0);

            Content = new FlowLayoutPanel();
            Content.AutoSize = true;
            Content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Content.FlowDirection = FlowDirection.TopDown;
            Content.WrapContents = false;
            Content.Margin = new Padding(0);
            Content.Padding = new Padding(0, 6, 0, 0);
            Content.BackColor = surfaceColor;
            viewport.Controls.Add(Content);

            viewport.Resize += delegate
            {
                UpdateContentLayout();
                SyncScrollBar();
            };
            Content.ControlAdded += delegate(object sender, ControlEventArgs e)
            {
                HookWheelRecursive(e.Control);
                UpdateContentLayout();
                SyncScrollBar();
            };
            Content.ControlRemoved += delegate
            {
                UpdateContentLayout();
                SyncScrollBar();
            };
            Content.SizeChanged += delegate { SyncScrollBar(); };
            Content.Layout += delegate { SyncScrollBar(); };

            HookWheelRecursive(this);
            HookWheelRecursive(viewport);
            HookWheelRecursive(Content);
            ApplySurfaceColor();
        }

        public int ContentClientWidth
        {
            get { return Math.Max(0, viewport.ClientSize.Width); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ForwardMouseWheel(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(surfaceColor);
        }

        private void ForwardMouseWheel(MouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            int maxScroll = GetMaximumScroll();
            if (maxScroll <= 0)
            {
                return;
            }

            int lines = SystemInformation.MouseWheelScrollLines;
            int delta = (e.Delta / 120) * Math.Max(1, lines) * 22;
            SetScrollValue(scrollValue - delta);
        }

        private void HookWheelRecursive(Control control)
        {
            if (control == null)
            {
                return;
            }

            control.MouseWheel -= ControlMouseWheel;
            control.MouseWheel += ControlMouseWheel;
            for (int i = 0; i < control.Controls.Count; i++)
            {
                HookWheelRecursive(control.Controls[i]);
            }
        }

        private void ControlMouseWheel(object sender, MouseEventArgs e)
        {
            ForwardMouseWheel(e);
        }

        private void UpdateContentLayout()
        {
            ApplyScrollBarLayout();
            int width = Math.Max(1, viewport.ClientSize.Width);
            if (Content.Width != width)
            {
                Content.Width = width;
            }
        }

        private void SetScrollValue(int value)
        {
            int maxScroll = GetMaximumScroll();
            int nextValue = Math.Max(0, Math.Min(maxScroll, value));
            if (scrollValue == nextValue)
            {
                scrollBar.SetMetrics(maxScroll, viewport.ClientSize.Height, scrollValue);
                return;
            }

            scrollValue = nextValue;
            Content.Location = new Point(0, -scrollValue);
            scrollBar.SetMetrics(maxScroll, viewport.ClientSize.Height, scrollValue);
        }

        private void SyncScrollBar()
        {
            ApplyScrollBarLayout();
            UpdateContentLayout();
            int maxScroll = GetMaximumScroll();
            if (scrollValue > maxScroll)
            {
                scrollValue = maxScroll;
            }

            Content.Location = new Point(0, -scrollValue);
            scrollBar.SetMetrics(maxScroll, viewport.ClientSize.Height, scrollValue);
        }

        private int GetMaximumScroll()
        {
            return Math.Max(0, Content.Height - viewport.ClientSize.Height);
        }

        private void ApplyScrollBarLayout()
        {
            bool showScrollBar = reserveScrollBarGutter || GetMaximumScroll() > 0;
            if (scrollBar.Visible != showScrollBar)
            {
                scrollBar.Visible = showScrollBar;
            }

            int gutterWidth = showScrollBar ? 18 : 0;
            ColumnStyle gutter = layout.ColumnStyles[1];
            if (gutter.Width != gutterWidth)
            {
                gutter.SizeType = SizeType.Absolute;
                gutter.Width = gutterWidth;
            }
        }

        private void ApplySurfaceColor()
        {
            BackColor = surfaceColor;
            if (layout != null)
            {
                layout.BackColor = surfaceColor;
            }
            if (viewport != null)
            {
                viewport.BackColor = surfaceColor;
            }
            if (Content != null)
            {
                Content.BackColor = surfaceColor;
            }
            if (scrollBar != null)
            {
                scrollBar.BackColor = surfaceColor;
            }
            Invalidate(true);
        }
    }

    internal sealed class DeviceListItemControl : Control
    {
        private bool hovered;
        private bool pressed;
        private AudioDeviceInfo device;
        private bool selected;
        private Bitmap cachedSurface;
        private Size cachedSurfaceSize;
        private int cachedSurfaceToken;
        public int CornerRadius = 14;

        public event EventHandler Activated;

        public AudioDeviceInfo Device
        {
            get { return device; }
            set
            {
                if (SameDevice(device, value))
                {
                    return;
                }
                device = value;
                Invalidate();
            }
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value)
                {
                    return;
                }
                selected = value;
                Invalidate();
            }
        }

        public DeviceListItemControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            Height = 64;
            Margin = new Padding(0, 0, 0, 10);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(UiTheme.ResolveParentBackColor(this));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            base.OnMouseEnter(e);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true;
            base.OnMouseDown(e);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            base.OnMouseUp(e);
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            EventHandler handler = Activated;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color backColor = UiTheme.ResolveParentBackColor(this);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color fillColor = UiTheme.Panel;
            Color borderColor = UiTheme.Border;
            if (selected)
            {
                fillColor = Color.FromArgb(28, 37, 50);
                borderColor = Color.FromArgb(88, 152, 232);
            }
            else if (pressed)
            {
                fillColor = Color.FromArgb(27, 31, 38);
            }
            else if (hovered)
            {
                fillColor = UiTheme.Hover;
            }

            int stateToken = UiTheme.HashState(
                Width,
                Height,
                backColor.ToArgb(),
                fillColor.ToArgb(),
                borderColor.ToArgb(),
                selected ? 1 : 0,
                hovered ? 1 : 0,
                pressed ? 1 : 0,
                CornerRadius);
            UiTheme.DrawCachedSupersampled(e.Graphics, Size, backColor, ref cachedSurface, ref cachedSurfaceSize, ref cachedSurfaceToken, stateToken, delegate(Graphics graphics)
            {
                RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (GraphicsPath path = UiTheme.CreateRoundedRectangle(rect, CornerRadius))
                using (SolidBrush fillBrush = new SolidBrush(fillColor))
                using (Pen borderPen = new Pen(borderColor, 1f))
                {
                    borderPen.Alignment = PenAlignment.Inset;
                    graphics.FillPath(fillBrush, path);
                    graphics.DrawPath(borderPen, path);
                }

                RectangleF accentRect = new RectangleF(14f, 13f, 6f, Height - 27f);
                using (GraphicsPath accentPath = UiTheme.CreateRoundedRectangle(accentRect, 3f))
                using (SolidBrush accentBrush = new SolidBrush(selected ? UiTheme.Accent : Color.FromArgb(52, 60, 70)))
                {
                    graphics.FillPath(accentBrush, accentPath);
                }
            });

            string name = device == null || string.IsNullOrEmpty(device.Name) ? "Unknown output" : device.Name;
            string caption = selected ? "Current output" : "Click to switch";

            Rectangle nameRect = new Rectangle(34, 12, Width - 152, 24);
            Rectangle captionRect = new Rectangle(34, 35, Width - 152, 18);
            Rectangle actionRect = new Rectangle(Width - 112, 0, 96, Height);

            TextRenderer.DrawText(
                e.Graphics,
                name,
                UiTheme.ItemTitleFont,
                nameRect,
                UiTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            TextRenderer.DrawText(
                e.Graphics,
                caption,
                UiTheme.CaptionFont,
                captionRect,
                selected ? Color.FromArgb(190, 213, 241) : UiTheme.MutedText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            TextRenderer.DrawText(
                e.Graphics,
                selected ? "Active" : "Switch",
                UiTheme.UiFontBold,
                actionRect,
                selected ? Color.FromArgb(190, 213, 241) : UiTheme.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UiTheme.DisposeBitmap(ref cachedSurface);
            }
            base.Dispose(disposing);
        }

        private static bool SameDevice(AudioDeviceInfo left, AudioDeviceInfo right)
        {
            if (object.ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                left.IsDefault == right.IsDefault;
        }
    }

    internal sealed class ActionPickerControl : Control
    {
        private readonly ContextMenuStrip menu;
        private readonly List<HotkeyActionOption> options;
        private bool hovered;
        private bool pressed;
        private bool focused;
        private HotkeyActionOption selectedOption;
        private Bitmap cachedSurface;
        private Size cachedSurfaceSize;
        private int cachedSurfaceToken;
        public int CornerRadius = 10;

        public event EventHandler SelectedOptionChanged;

        public HotkeyActionOption SelectedOption
        {
            get { return selectedOption; }
            set
            {
                if (selectedOption == value)
                {
                    return;
                }

                selectedOption = value;
                Invalidate();
                EventHandler handler = SelectedOptionChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        public ActionPickerControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);

            Height = 36;
            Cursor = Cursors.Hand;
            Font = UiTheme.UiFont;
            ForeColor = UiTheme.Text;
            BackColor = UiTheme.Panel;
            TabStop = true;
            options = new List<HotkeyActionOption>();

            menu = new ContextMenuStrip();
            UiTheme.ApplyMenu(menu);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                menu.Dispose();
                UiTheme.DisposeBitmap(ref cachedSurface);
            }
            base.Dispose(disposing);
        }

        public void SetOptions(List<HotkeyActionOption> items)
        {
            options.Clear();
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    options.Add(items[i]);
                }
            }
            RebuildMenu();
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            base.OnMouseEnter(e);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            base.OnMouseLeave(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = true;
            base.OnMouseDown(e);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            base.OnMouseUp(e);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            focused = true;
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            focused = false;
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter || keyData == Keys.Down)
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            ShowMenu();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down)
            {
                ShowMenu();
                e.SuppressKeyPress = true;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(UiTheme.ResolveParentBackColor(this));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color backColor = UiTheme.ResolveParentBackColor(this);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color fillColor = pressed
                ? Color.FromArgb(32, 36, 44)
                : hovered
                    ? Color.FromArgb(36, 40, 49)
                    : UiTheme.PanelElevated;
            Color borderColor = focused ? Color.FromArgb(108, 174, 255) : UiTheme.Border;
            int stateToken = UiTheme.HashState(
                Width,
                Height,
                backColor.ToArgb(),
                fillColor.ToArgb(),
                borderColor.ToArgb(),
                focused ? 1 : 0,
                hovered ? 1 : 0,
                pressed ? 1 : 0,
                CornerRadius);
            UiTheme.DrawCachedSupersampled(e.Graphics, Size, backColor, ref cachedSurface, ref cachedSurfaceSize, ref cachedSurfaceToken, stateToken, delegate(Graphics graphics)
            {
                RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (GraphicsPath path = UiTheme.CreateRoundedRectangle(rect, CornerRadius))
                using (SolidBrush fillBrush = new SolidBrush(fillColor))
                using (Pen borderPen = new Pen(borderColor, 1f))
                {
                    borderPen.Alignment = PenAlignment.Inset;
                    graphics.FillPath(fillBrush, path);
                    graphics.DrawPath(borderPen, path);
                }
            });

            Rectangle textRect = new Rectangle(14, 0, Width - 44, Height);
            string text = selectedOption == null ? "Select action" : selectedOption.DisplayName;
            TextRenderer.DrawText(
                e.Graphics,
                text,
                Font,
                textRect,
                selectedOption == null ? UiTheme.MutedText : UiTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            using (Pen chevronPen = new Pen(UiTheme.MutedText, 1.5f))
            {
                chevronPen.StartCap = LineCap.Round;
                chevronPen.EndCap = LineCap.Round;
                int midX = Width - 18;
                int midY = Height / 2 + 1;
                e.Graphics.DrawLine(chevronPen, midX - 4, midY - 2, midX, midY + 2);
                e.Graphics.DrawLine(chevronPen, midX, midY + 2, midX + 4, midY - 2);
            }
        }

        private void RebuildMenu()
        {
            menu.Items.Clear();
            for (int i = 0; i < options.Count; i++)
            {
                HotkeyActionOption option = options[i];
                ToolStripMenuItem item = new ToolStripMenuItem(option.DisplayName);
                item.Tag = option;
                item.Checked = option == selectedOption;
                UiTheme.ApplyMenuItem(item);
                item.Click += delegate(object sender, EventArgs e)
                {
                    ToolStripMenuItem clicked = (ToolStripMenuItem)sender;
                    SelectedOption = (HotkeyActionOption)clicked.Tag;
                    RebuildMenu();
                };
                menu.Items.Add(item);
            }
        }

        private void ShowMenu()
        {
            if (menu.Items.Count == 0)
            {
                return;
            }

            Focus();
            RebuildMenu();
            menu.Show(this, new Point(0, Height + 4));
        }
    }

    internal sealed class DashboardForm : CompositedForm
    {
        private readonly Func<AppSettings> getSettings;
        private readonly Func<bool, List<AudioDeviceInfo>> getDevices;
        private readonly Action<AudioDeviceInfo> switchDevice;
        private readonly Action openHotkeySettings;
        private readonly Action afterStartupChanged;
        private readonly Label currentDeviceLabel;
        private readonly Label currentDeviceMetaLabel;
        private readonly CodexFlowScrollHost deviceListScrollHost;
        private readonly FlowLayoutPanel deviceListPanel;
        private readonly Label deviceListStatusLabel;
        private readonly Dictionary<string, DeviceListItemControl> deviceItemsById;
        private readonly ToggleSwitchControl startupToggle;
        private readonly Label startupStatusLabel;
        private readonly Label hotkeySummaryLabel;
        private readonly ThemedButton refreshButton;
        private bool loading;
        private bool refreshInProgress;
        private bool refreshPending;
        private bool pendingForceRefresh;
        private int refreshVersion;
        private bool hasLoadedData;
        private DateTime lastRefreshUtc = DateTime.MinValue;

        public DashboardForm(
            Func<AppSettings> getSettings,
            Func<bool, List<AudioDeviceInfo>> getDevices,
            Action<AudioDeviceInfo> switchDevice,
            Action openHotkeySettings,
            Action afterStartupChanged)
        {
            this.getSettings = getSettings;
            this.getDevices = getDevices;
            this.switchDevice = switchDevice;
            this.openHotkeySettings = openHotkeySettings;
            this.afterStartupChanged = afterStartupChanged;
            deviceItemsById = new Dictionary<string, DeviceListItemControl>(StringComparer.OrdinalIgnoreCase);

            Text = "Audio Output Switcher";
            Width = 920;
            Height = 660;
            MinimumSize = new Size(820, 560);
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon.Load();
            UiTheme.ApplyForm(this);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = UiTheme.AppBackground;
            root.Padding = new Padding(24, 22, 24, 24);
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.BackColor = UiTheme.AppBackground;
            header.ColumnCount = 1;
            header.RowCount = 2;
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.Controls.Add(header, 0, 0);

            Label titleLabel = new Label();
            titleLabel.Text = "Audio Output Switcher";
            titleLabel.Dock = DockStyle.Fill;
            titleLabel.TextAlign = ContentAlignment.BottomLeft;
            titleLabel.Font = UiTheme.TitleFont;
            titleLabel.ForeColor = UiTheme.Text;
            titleLabel.BackColor = UiTheme.AppBackground;
            header.Controls.Add(titleLabel, 0, 0);

            Label subtitleLabel = new Label();
            subtitleLabel.Text = "Switch outputs, review active devices, and manage shortcuts from one place.";
            subtitleLabel.Dock = DockStyle.Fill;
            subtitleLabel.TextAlign = ContentAlignment.TopLeft;
            UiTheme.ApplyLabel(subtitleLabel, true);
            subtitleLabel.BackColor = UiTheme.AppBackground;
            header.Controls.Add(subtitleLabel, 0, 1);

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.BackColor = UiTheme.AppBackground;
            body.ColumnCount = 2;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 312));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(body, 0, 1);

            TableLayoutPanel leftRail = new TableLayoutPanel();
            leftRail.Dock = DockStyle.Fill;
            leftRail.BackColor = UiTheme.AppBackground;
            leftRail.Margin = new Padding(0, 0, 18, 0);
            leftRail.ColumnCount = 1;
            leftRail.RowCount = 4;
            leftRail.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            leftRail.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            leftRail.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
            leftRail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(leftRail, 0, 0);

            CardPanel currentCard = new CardPanel();
            currentCard.Dock = DockStyle.Fill;
            currentCard.Padding = new Padding(18);
            currentCard.Margin = new Padding(0, 0, 0, 14);
            leftRail.Controls.Add(currentCard, 0, 0);

            TableLayoutPanel currentLayout = new TableLayoutPanel();
            currentLayout.Dock = DockStyle.Fill;
            currentLayout.BackColor = currentCard.FillColor;
            currentLayout.ColumnCount = 1;
            currentLayout.RowCount = 3;
            currentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            currentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            currentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            currentCard.Controls.Add(currentLayout);

            Label currentTitleLabel = CreateSectionLabel("CURRENT OUTPUT");
            currentTitleLabel.BackColor = currentCard.FillColor;
            currentLayout.Controls.Add(currentTitleLabel, 0, 0);

            currentDeviceLabel = new Label();
            currentDeviceLabel.Dock = DockStyle.Fill;
            currentDeviceLabel.TextAlign = ContentAlignment.MiddleLeft;
            currentDeviceLabel.Font = UiTheme.EmphasisFont;
            currentDeviceLabel.ForeColor = UiTheme.Text;
            currentDeviceLabel.BackColor = currentCard.FillColor;
            currentDeviceLabel.AutoEllipsis = true;
            currentLayout.Controls.Add(currentDeviceLabel, 0, 1);

            currentDeviceMetaLabel = new Label();
            currentDeviceMetaLabel.Dock = DockStyle.Fill;
            currentDeviceMetaLabel.TextAlign = ContentAlignment.TopLeft;
            currentDeviceMetaLabel.ForeColor = UiTheme.MutedText;
            currentDeviceMetaLabel.Font = UiTheme.CaptionFont;
            currentDeviceMetaLabel.Text = "Loading current playback device.";
            currentDeviceMetaLabel.BackColor = currentCard.FillColor;
            currentLayout.Controls.Add(currentDeviceMetaLabel, 0, 2);

            CardPanel startupCard = new CardPanel();
            startupCard.Dock = DockStyle.Fill;
            startupCard.Padding = new Padding(18, 16, 18, 16);
            startupCard.Margin = new Padding(0, 0, 0, 14);
            leftRail.Controls.Add(startupCard, 0, 1);

            TableLayoutPanel startupLayout = new TableLayoutPanel();
            startupLayout.Dock = DockStyle.Fill;
            startupLayout.BackColor = startupCard.FillColor;
            startupLayout.ColumnCount = 2;
            startupLayout.RowCount = 3;
            startupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            startupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            startupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            startupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            startupLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            startupCard.Controls.Add(startupLayout);

            Label startupTitleLabel = CreateSectionLabel("START WITH WINDOWS");
            startupTitleLabel.BackColor = startupCard.FillColor;
            startupLayout.Controls.Add(startupTitleLabel, 0, 0);
            startupLayout.SetColumnSpan(startupTitleLabel, 2);

            startupStatusLabel = new Label();
            startupStatusLabel.Dock = DockStyle.Fill;
            startupStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            startupStatusLabel.Font = UiTheme.SectionFont;
            startupStatusLabel.ForeColor = UiTheme.Text;
            startupStatusLabel.BackColor = startupCard.FillColor;
            startupLayout.Controls.Add(startupStatusLabel, 0, 1);

            startupToggle = new ToggleSwitchControl();
            startupToggle.Anchor = AnchorStyles.Right;
            startupToggle.Margin = new Padding(0, 0, 0, 0);
            startupToggle.CheckedChanged += delegate
            {
                if (loading)
                {
                    return;
                }

                try
                {
                    AppSettings current = getSettings();
                    bool runAsAdministrator = current != null && current.RunAsAdministrator;
                    StartupManager.SetEnabled(startupToggle.Checked, runAsAdministrator);
                    UpdateStartupStatus();
                    if (afterStartupChanged != null)
                    {
                        afterStartupChanged();
                    }
                }
                catch (Exception ex)
                {
                    loading = true;
                    startupToggle.Checked = !startupToggle.Checked;
                    loading = false;
                    UpdateStartupStatus();
                    MessageBox.Show(this, ex.Message, "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            startupLayout.Controls.Add(startupToggle, 1, 1);

            Label startupHintLabel = new Label();
            startupHintLabel.Dock = DockStyle.Fill;
            startupHintLabel.Text = "Keeps the switcher ready after sign-in.";
            startupHintLabel.ForeColor = UiTheme.MutedText;
            startupHintLabel.Font = UiTheme.CaptionFont;
            startupHintLabel.BackColor = startupCard.FillColor;
            startupLayout.Controls.Add(startupHintLabel, 0, 2);
            startupLayout.SetColumnSpan(startupHintLabel, 2);

            CardPanel hotkeyCard = new CardPanel();
            hotkeyCard.Dock = DockStyle.Fill;
            hotkeyCard.Padding = new Padding(18, 16, 18, 18);
            hotkeyCard.Margin = new Padding(0);
            leftRail.Controls.Add(hotkeyCard, 0, 2);

            TableLayoutPanel hotkeyLayout = new TableLayoutPanel();
            hotkeyLayout.Dock = DockStyle.Fill;
            hotkeyLayout.BackColor = hotkeyCard.FillColor;
            hotkeyLayout.ColumnCount = 1;
            hotkeyLayout.RowCount = 4;
            hotkeyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            hotkeyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            hotkeyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            hotkeyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            hotkeyCard.Controls.Add(hotkeyLayout);

            Label hotkeyTitleLabel = CreateSectionLabel("HOTKEYS");
            hotkeyTitleLabel.BackColor = hotkeyCard.FillColor;
            hotkeyLayout.Controls.Add(hotkeyTitleLabel, 0, 0);

            Label hotkeyHintLabel = new Label();
            hotkeyHintLabel.Dock = DockStyle.Fill;
            hotkeyHintLabel.Text = "Keyboard, mouse, and combo shortcuts";
            hotkeyHintLabel.ForeColor = UiTheme.MutedText;
            hotkeyHintLabel.Font = UiTheme.CaptionFont;
            hotkeyHintLabel.BackColor = hotkeyCard.FillColor;
            hotkeyLayout.Controls.Add(hotkeyHintLabel, 0, 1);

            hotkeySummaryLabel = new Label();
            hotkeySummaryLabel.Dock = DockStyle.Fill;
            hotkeySummaryLabel.TextAlign = ContentAlignment.TopLeft;
            hotkeySummaryLabel.Font = UiTheme.UiFont;
            hotkeySummaryLabel.ForeColor = UiTheme.Text;
            hotkeySummaryLabel.BackColor = hotkeyCard.FillColor;
            hotkeyLayout.Controls.Add(hotkeySummaryLabel, 0, 2);

            ThemedButton hotkeyButton = new ThemedButton();
            hotkeyButton.Text = "Configure";
            hotkeyButton.Width = 120;
            hotkeyButton.Dock = DockStyle.Right;
            UiTheme.ApplyButton(hotkeyButton, true);
            hotkeyButton.Click += delegate
            {
                if (openHotkeySettings != null)
                {
                    openHotkeySettings();
                    RefreshData();
                }
            };
            hotkeyLayout.Controls.Add(hotkeyButton, 0, 3);

            CardPanel deviceCard = new CardPanel();
            deviceCard.Dock = DockStyle.Fill;
            deviceCard.Padding = new Padding(18);
            deviceCard.Margin = new Padding(0);
            body.Controls.Add(deviceCard, 1, 0);

            TableLayoutPanel deviceLayout = new TableLayoutPanel();
            deviceLayout.Dock = DockStyle.Fill;
            deviceLayout.BackColor = deviceCard.FillColor;
            deviceLayout.ColumnCount = 1;
            deviceLayout.RowCount = 3;
            deviceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            deviceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            deviceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            deviceCard.Controls.Add(deviceLayout);

            TableLayoutPanel deviceHeader = new TableLayoutPanel();
            deviceHeader.Dock = DockStyle.Fill;
            deviceHeader.BackColor = deviceCard.FillColor;
            deviceHeader.ColumnCount = 2;
            deviceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            deviceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            deviceLayout.Controls.Add(deviceHeader, 0, 0);

            Label devicesTitle = new Label();
            devicesTitle.Text = "Audio devices";
            devicesTitle.Dock = DockStyle.Fill;
            devicesTitle.TextAlign = ContentAlignment.MiddleLeft;
            devicesTitle.Font = UiTheme.SectionTitleLargeFont;
            devicesTitle.ForeColor = UiTheme.Text;
            devicesTitle.BackColor = deviceCard.FillColor;
            deviceHeader.Controls.Add(devicesTitle, 0, 0);

            refreshButton = new ThemedButton();
            refreshButton.Text = "Refresh";
            refreshButton.Dock = DockStyle.None;
            refreshButton.Anchor = AnchorStyles.Right;
            refreshButton.Size = new Size(118, 36);
            refreshButton.Margin = new Padding(0, 2, 0, 2);
            UiTheme.ApplyButton(refreshButton, true);
            refreshButton.Click += delegate { RefreshData(true); };
            deviceHeader.Controls.Add(refreshButton, 1, 0);

            Label deviceHintLabel = new Label();
            deviceHintLabel.Text = "Click any device to make it the default playback output.";
            deviceHintLabel.Dock = DockStyle.Fill;
            deviceHintLabel.TextAlign = ContentAlignment.MiddleLeft;
            deviceHintLabel.ForeColor = UiTheme.MutedText;
            deviceHintLabel.Font = UiTheme.CaptionFont;
            deviceHintLabel.BackColor = deviceCard.FillColor;
            deviceLayout.Controls.Add(deviceHintLabel, 0, 1);

            Panel deviceListHost = new Panel();
            deviceListHost.Dock = DockStyle.Fill;
            deviceListHost.BackColor = deviceCard.FillColor;
            deviceLayout.Controls.Add(deviceListHost, 0, 2);

            deviceListScrollHost = new CodexFlowScrollHost();
            deviceListScrollHost.Dock = DockStyle.Fill;
            deviceListScrollHost.Margin = new Padding(0);
            deviceListScrollHost.BackColor = deviceCard.FillColor;
            deviceListScrollHost.SurfaceColor = deviceCard.FillColor;
            deviceListScrollHost.ReserveScrollBarGutter = false;
            deviceListHost.Controls.Add(deviceListScrollHost);

            deviceListPanel = deviceListScrollHost.Content;
            deviceListPanel.Dock = DockStyle.None;
            deviceListPanel.Padding = new Padding(0, 6, 0, 0);
            deviceListPanel.BackColor = deviceCard.FillColor;
            deviceListPanel.Resize += delegate { ResizeDeviceItems(); };
            deviceListScrollHost.Resize += delegate { ResizeDeviceItems(); };

            deviceListStatusLabel = CreateMutedLabel(string.Empty);
            deviceListStatusLabel.Dock = DockStyle.Fill;
            deviceListStatusLabel.Padding = new Padding(12, 10, 12, 10);
            deviceListStatusLabel.BackColor = deviceCard.FillColor;
            deviceListStatusLabel.Visible = false;
            deviceListHost.Controls.Add(deviceListStatusLabel);

            UiTheme.EnableDoubleBufferingRecursive(this);
            UpdateStartupStatus();
            UpdateHotkeySummary();
            ApplyLoadingState();
            PrimeVisualCache();
        }

        public void RefreshData()
        {
            RefreshData(false);
        }

        public void RefreshData(bool forceRefresh)
        {
            UpdateStartupStatus();
            UpdateHotkeySummary();
            if (refreshInProgress)
            {
                refreshPending = true;
                pendingForceRefresh = pendingForceRefresh || forceRefresh;
                return;
            }

            refreshInProgress = true;
            int currentRefreshVersion = Interlocked.Increment(ref refreshVersion);
            bool showLoadingState = !hasLoadedData;
            if (showLoadingState)
            {
                ApplyLoadingState();
            }

            refreshButton.Enabled = false;

            Thread worker = new Thread(
                delegate()
                {
                    List<AudioDeviceInfo> currentDevices = null;
                    Exception error = null;
                    try
                    {
                        currentDevices = getDevices(forceRefresh);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }

                    if (IsDisposed)
                    {
                        return;
                    }

                    try
                    {
                        BeginInvoke(
                            (MethodInvoker)delegate
                            {
                                if (IsDisposed || currentRefreshVersion != refreshVersion)
                                {
                                    return;
                                }

                                try
                                {
                                    if (error != null)
                                    {
                                        ApplyRefreshError(error);
                                    }
                                    else
                                    {
                                        ApplyRefreshResult(currentDevices ?? new List<AudioDeviceInfo>());
                                    }
                                }
                                finally
                                {
                                    CompleteRefresh();
                                }
                            });
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });
            worker.IsBackground = true;
            worker.Name = "AudioOutputSwitcher.DashboardRefresh";
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        private void CompleteRefresh()
        {
            refreshInProgress = false;
            if (refreshPending)
            {
                bool forceRefresh = pendingForceRefresh;
                refreshPending = false;
                pendingForceRefresh = false;
                RefreshData(forceRefresh);
                return;
            }

            refreshButton.Enabled = true;
        }

        public void PrepareForDisplay()
        {
            if (Visible || IsDisposed || Width <= 0 || Height <= 0)
            {
                return;
            }

            if (UiTheme.TryWarmUpForm(this))
            {
                PrimeVisualCache();
            }
        }

        public bool IsRefreshStale(TimeSpan maxAge)
        {
            if (!hasLoadedData)
            {
                return true;
            }

            return (DateTime.UtcNow - lastRefreshUtc) >= maxAge;
        }

        private void UpdateStartupStatus()
        {
            bool enabled = StartupManager.IsEnabled();
            loading = true;
            startupToggle.Checked = enabled;
            loading = false;
            startupStatusLabel.Text = enabled ? "Enabled" : "Disabled";
            startupStatusLabel.ForeColor = enabled ? UiTheme.Text : UiTheme.MutedText;
        }

        private void UpdateHotkeySummary()
        {
            AppSettings settings = getSettings();
            if (settings == null || settings.Hotkeys.Count == 0)
            {
                hotkeySummaryLabel.Text = "No hotkeys configured";
                hotkeySummaryLabel.ForeColor = UiTheme.MutedText;
                return;
            }

            List<string> lines = new List<string>();
            int count = Math.Min(3, settings.Hotkeys.Count);
            for (int i = 0; i < count; i++)
            {
                HotkeyBinding binding = settings.Hotkeys[i];
                if (binding.Action != null && binding.Hotkey != null)
                {
                    lines.Add(binding.Action.ToDisplayName() + Environment.NewLine + binding.Hotkey.ToConfigString());
                }
            }
            if (settings.Hotkeys.Count > count)
            {
                lines.Add("+" + (settings.Hotkeys.Count - count).ToString() + " more");
            }

            hotkeySummaryLabel.Text = string.Join(Environment.NewLine, lines.ToArray());
            hotkeySummaryLabel.ForeColor = UiTheme.Text;
        }

        private void RebuildDeviceList(List<AudioDeviceInfo> currentDevices)
        {
            if (currentDevices.Count == 0)
            {
                HideUnusedDeviceItems(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                ShowDeviceListStatus("No active output devices");
                return;
            }

            int width = Math.Max(280, deviceListPanel.ClientSize.Width - 2);
            HashSet<string> activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            deviceListPanel.SuspendLayout();
            try
            {
                for (int i = 0; i < currentDevices.Count; i++)
                {
                    AudioDeviceInfo device = currentDevices[i];
                    string deviceKey = GetDeviceControlKey(device, i);
                    DeviceListItemControl deviceItem;
                    if (!deviceItemsById.TryGetValue(deviceKey, out deviceItem))
                    {
                        deviceItem = CreateDeviceItemControl();
                        deviceItemsById[deviceKey] = deviceItem;
                        deviceListPanel.Controls.Add(deviceItem);
                    }

                    activeIds.Add(deviceKey);
                    deviceItem.Device = device;
                    deviceItem.Selected = device.IsDefault;
                    deviceItem.Tag = device;
                    if (deviceItem.Width != width)
                    {
                        deviceItem.Width = width;
                    }
                    if (deviceListPanel.Controls.GetChildIndex(deviceItem) != i)
                    {
                        deviceListPanel.Controls.SetChildIndex(deviceItem, i);
                    }
                }
            }
            finally
            {
                deviceListPanel.ResumeLayout(true);
            }

            HideUnusedDeviceItems(activeIds);
            ShowDeviceListItems();
        }

        private void ApplyLoadingState()
        {
            currentDeviceLabel.Text = "Loading audio devices...";
            currentDeviceMetaLabel.Text = "Fetching current output and available playback devices.";
            ShowDeviceListStatus("Loading available devices...");
        }

        private void ApplyRefreshError(Exception ex)
        {
            lastRefreshUtc = DateTime.UtcNow;
            currentDeviceLabel.Text = "Could not read audio devices";
            currentDeviceMetaLabel.Text = "Check Windows audio services and device availability.";
            ShowDeviceListStatus(ex.Message);
            PrimeVisualCache();
        }

        private void ApplyRefreshResult(List<AudioDeviceInfo> currentDevices)
        {
            hasLoadedData = true;
            lastRefreshUtc = DateTime.UtcNow;

            AudioDeviceInfo current = null;
            for (int i = 0; i < currentDevices.Count; i++)
            {
                if (currentDevices[i].IsDefault)
                {
                    current = currentDevices[i];
                    break;
                }
            }

            currentDeviceLabel.Text = current == null ? "No active output device" : current.Name;
            currentDeviceMetaLabel.Text = current == null
                ? "Connect or enable a playback device to switch outputs."
                : "This device is currently set as the Windows default output.";
            RebuildDeviceList(currentDevices);
            PrimeVisualCache();
        }

        private void PrimeVisualCache()
        {
            if (Visible || IsDisposed || Width <= 0 || Height <= 0)
            {
                return;
            }

            try
            {
                if (!IsHandleCreated)
                {
                    IntPtr unused = Handle;
                }

                CreateControl();
                PerformLayout();
                using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
                {
                    DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                }
            }
            catch
            {
            }
        }

        private void ResizeDeviceItems()
        {
            int width = Math.Max(280, deviceListPanel.ClientSize.Width - 2);
            for (int i = 0; i < deviceListPanel.Controls.Count; i++)
            {
                if (deviceListPanel.Controls[i].Width != width)
                {
                    deviceListPanel.Controls[i].Width = width;
                }
            }
        }

        private DeviceListItemControl CreateDeviceItemControl()
        {
            DeviceListItemControl deviceItem = new DeviceListItemControl();
            deviceItem.Activated += OnDeviceItemActivated;
            return deviceItem;
        }

        private void OnDeviceItemActivated(object sender, EventArgs e)
        {
            DeviceListItemControl clicked = sender as DeviceListItemControl;
            AudioDeviceInfo target = clicked == null ? null : clicked.Tag as AudioDeviceInfo;
            if (target == null || switchDevice == null)
            {
                return;
            }

            switchDevice(target);
            RefreshData();
        }

        private void HideUnusedDeviceItems(HashSet<string> activeIds)
        {
            List<string> removedIds = new List<string>();
            foreach (KeyValuePair<string, DeviceListItemControl> pair in deviceItemsById)
            {
                if (activeIds.Contains(pair.Key))
                {
                    continue;
                }

                if (deviceListPanel.Controls.Contains(pair.Value))
                {
                    deviceListPanel.Controls.Remove(pair.Value);
                }
                pair.Value.Dispose();
                removedIds.Add(pair.Key);
            }

            for (int i = 0; i < removedIds.Count; i++)
            {
                deviceItemsById.Remove(removedIds[i]);
            }
        }

        private void ShowDeviceListStatus(string text)
        {
            deviceListStatusLabel.Text = text;
            deviceListStatusLabel.Visible = true;
            deviceListStatusLabel.BringToFront();
            deviceListScrollHost.Visible = false;
        }

        private void ShowDeviceListItems()
        {
            deviceListStatusLabel.Visible = false;
            deviceListScrollHost.Visible = true;
            deviceListScrollHost.BringToFront();
        }

        private static string GetDeviceControlKey(AudioDeviceInfo device, int index)
        {
            if (device != null)
            {
                if (!string.IsNullOrEmpty(device.Id))
                {
                    return device.Id;
                }

                if (!string.IsNullOrEmpty(device.Name))
                {
                    return "name:" + device.Name;
                }
            }

            return "index:" + index.ToString();
        }

        private static Label CreateMutedLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Padding = new Padding(12, 0, 12, 0);
            UiTheme.ApplyLabel(label, true);
            return label;
        }

        private static Label CreateSectionLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Font = UiTheme.CaptionFont;
            label.ForeColor = UiTheme.MutedText;
            label.BackColor = Color.Transparent;
            return label;
        }
    }

    internal sealed class HotkeySettingsForm : CompositedForm
    {
        private readonly AppSettings originalSettings;
        private readonly List<AudioDeviceInfo> devices;
        private readonly List<HotkeyActionOption> options;
        private readonly List<HotkeyRowControl> rows;
        private readonly CodexFlowScrollHost rowsScrollHost;
        private readonly FlowLayoutPanel rowsPanel;
        private readonly ToggleSwitchControl showBalloonToggle;
        private readonly ToggleSwitchControl startupToggle;
        private readonly ToggleSwitchControl runAsAdministratorToggle;
        private readonly Label showBalloonStatusLabel;
        private readonly Label startupStatusLabel;
        private readonly Label runAsAdministratorStatusLabel;

        public AppSettings Settings { get; private set; }

        public HotkeySettingsForm(AppSettings currentSettings, List<AudioDeviceInfo> currentDevices)
        {
            SuspendLayout();
            originalSettings = currentSettings;
            devices = currentDevices == null ? new List<AudioDeviceInfo>() : currentDevices;
            options = BuildActionOptions(devices);
            rows = new List<HotkeyRowControl>();

            Text = "Hotkey settings";
            Width = 920;
            Height = 660;
            MinimumSize = new Size(780, 520);
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon.Load();
            UiTheme.ApplyForm(this);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = UiTheme.AppBackground;
            root.Padding = new Padding(24, 22, 24, 24);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            Controls.Add(root);

            TableLayoutPanel headerBlock = new TableLayoutPanel();
            headerBlock.Dock = DockStyle.Fill;
            headerBlock.BackColor = UiTheme.AppBackground;
            headerBlock.ColumnCount = 1;
            headerBlock.RowCount = 2;
            headerBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            headerBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.Controls.Add(headerBlock, 0, 0);

            Label titleLabel = new Label();
            titleLabel.Text = "Hotkey settings";
            titleLabel.Dock = DockStyle.Fill;
            titleLabel.TextAlign = ContentAlignment.BottomLeft;
            titleLabel.Font = UiTheme.TitleFont;
            titleLabel.ForeColor = UiTheme.Text;
            titleLabel.BackColor = UiTheme.AppBackground;
            headerBlock.Controls.Add(titleLabel, 0, 0);

            Label subtitleLabel = new Label();
            subtitleLabel.Text = "Configure keyboard, mouse, and combo shortcuts for switching output devices.";
            subtitleLabel.Dock = DockStyle.Fill;
            subtitleLabel.TextAlign = ContentAlignment.TopLeft;
            subtitleLabel.ForeColor = UiTheme.MutedText;
            subtitleLabel.Font = UiTheme.CaptionFont;
            subtitleLabel.BackColor = UiTheme.AppBackground;
            headerBlock.Controls.Add(subtitleLabel, 0, 1);

            TableLayoutPanel settingsRow = new TableLayoutPanel();
            settingsRow.Dock = DockStyle.Fill;
            settingsRow.BackColor = UiTheme.AppBackground;
            settingsRow.ColumnCount = 3;
            settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333f));
            settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333f));
            settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333f));
            root.Controls.Add(settingsRow, 0, 1);

            CardPanel notificationsCard = new CardPanel();
            notificationsCard.Dock = DockStyle.Fill;
            notificationsCard.Padding = new Padding(18, 16, 18, 16);
            notificationsCard.Margin = new Padding(0, 0, 10, 0);
            settingsRow.Controls.Add(notificationsCard, 0, 0);

            TableLayoutPanel notificationsLayout = new TableLayoutPanel();
            notificationsLayout.Dock = DockStyle.Fill;
            notificationsLayout.BackColor = notificationsCard.FillColor;
            notificationsLayout.ColumnCount = 2;
            notificationsLayout.RowCount = 3;
            notificationsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            notificationsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            notificationsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            notificationsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            notificationsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            notificationsCard.Controls.Add(notificationsLayout);

            Label notificationsTitle = new Label();
            notificationsTitle.Text = "SYSTEM NOTIFICATIONS";
            notificationsTitle.Dock = DockStyle.Fill;
            notificationsTitle.TextAlign = ContentAlignment.MiddleLeft;
            notificationsTitle.ForeColor = UiTheme.MutedText;
            notificationsTitle.Font = UiTheme.CaptionFont;
            notificationsTitle.BackColor = notificationsCard.FillColor;
            notificationsLayout.Controls.Add(notificationsTitle, 0, 0);
            notificationsLayout.SetColumnSpan(notificationsTitle, 2);

            showBalloonStatusLabel = new Label();
            showBalloonStatusLabel.Dock = DockStyle.Fill;
            showBalloonStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            showBalloonStatusLabel.Font = UiTheme.SectionFont;
            showBalloonStatusLabel.ForeColor = UiTheme.Text;
            showBalloonStatusLabel.BackColor = notificationsCard.FillColor;
            notificationsLayout.Controls.Add(showBalloonStatusLabel, 0, 1);

            showBalloonToggle = new ToggleSwitchControl();
            showBalloonToggle.Checked = currentSettings.ShowBalloon;
            showBalloonToggle.Anchor = AnchorStyles.Right;
            showBalloonToggle.Margin = new Padding(0);
            showBalloonToggle.CheckedChanged += delegate
            {
                UpdateNotificationStatus();
            };
            notificationsLayout.Controls.Add(showBalloonToggle, 1, 1);

            Label notificationsHint = new Label();
            notificationsHint.Text = "Shows switch confirmations and settings updates. Errors remain visible.";
            notificationsHint.Dock = DockStyle.Fill;
            notificationsHint.ForeColor = UiTheme.MutedText;
            notificationsHint.Font = UiTheme.CaptionFont;
            notificationsHint.BackColor = notificationsCard.FillColor;
            notificationsLayout.Controls.Add(notificationsHint, 0, 2);
            notificationsLayout.SetColumnSpan(notificationsHint, 2);

            CardPanel startupCard = new CardPanel();
            startupCard.Dock = DockStyle.Fill;
            startupCard.Padding = new Padding(18, 16, 18, 16);
            startupCard.Margin = new Padding(5, 0, 5, 0);
            settingsRow.Controls.Add(startupCard, 1, 0);

            TableLayoutPanel startupLayout = new TableLayoutPanel();
            startupLayout.Dock = DockStyle.Fill;
            startupLayout.BackColor = startupCard.FillColor;
            startupLayout.ColumnCount = 2;
            startupLayout.RowCount = 3;
            startupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            startupLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            startupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            startupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            startupLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            startupCard.Controls.Add(startupLayout);

            Label startupTitle = new Label();
            startupTitle.Text = "START WITH WINDOWS";
            startupTitle.Dock = DockStyle.Fill;
            startupTitle.TextAlign = ContentAlignment.MiddleLeft;
            startupTitle.ForeColor = UiTheme.MutedText;
            startupTitle.Font = UiTheme.CaptionFont;
            startupTitle.BackColor = startupCard.FillColor;
            startupLayout.Controls.Add(startupTitle, 0, 0);
            startupLayout.SetColumnSpan(startupTitle, 2);

            startupStatusLabel = new Label();
            startupStatusLabel.Dock = DockStyle.Fill;
            startupStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            startupStatusLabel.Font = UiTheme.SectionFont;
            startupStatusLabel.ForeColor = UiTheme.Text;
            startupStatusLabel.BackColor = startupCard.FillColor;
            startupLayout.Controls.Add(startupStatusLabel, 0, 1);

            startupToggle = new ToggleSwitchControl();
            startupToggle.Checked = StartupManager.IsEnabled();
            startupToggle.Anchor = AnchorStyles.Right;
            startupToggle.Margin = new Padding(0);
            startupToggle.CheckedChanged += delegate { UpdateStartupStatusLabel(); };
            startupLayout.Controls.Add(startupToggle, 1, 1);

            Label startupHint = new Label();
            startupHint.Text = "Launches the switcher automatically after Windows sign-in.";
            startupHint.Dock = DockStyle.Fill;
            startupHint.ForeColor = UiTheme.MutedText;
            startupHint.Font = UiTheme.CaptionFont;
            startupHint.BackColor = startupCard.FillColor;
            startupLayout.Controls.Add(startupHint, 0, 2);
            startupLayout.SetColumnSpan(startupHint, 2);

            CardPanel adminCard = new CardPanel();
            adminCard.Dock = DockStyle.Fill;
            adminCard.Padding = new Padding(18, 16, 18, 16);
            adminCard.Margin = new Padding(10, 0, 0, 0);
            settingsRow.Controls.Add(adminCard, 2, 0);

            TableLayoutPanel adminLayout = new TableLayoutPanel();
            adminLayout.Dock = DockStyle.Fill;
            adminLayout.BackColor = adminCard.FillColor;
            adminLayout.ColumnCount = 2;
            adminLayout.RowCount = 3;
            adminLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            adminLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            adminLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            adminLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            adminLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            adminCard.Controls.Add(adminLayout);

            Label adminTitle = new Label();
            adminTitle.Text = "RUN AS ADMINISTRATOR";
            adminTitle.Dock = DockStyle.Fill;
            adminTitle.TextAlign = ContentAlignment.MiddleLeft;
            adminTitle.ForeColor = UiTheme.MutedText;
            adminTitle.Font = UiTheme.CaptionFont;
            adminTitle.BackColor = adminCard.FillColor;
            adminLayout.Controls.Add(adminTitle, 0, 0);
            adminLayout.SetColumnSpan(adminTitle, 2);

            runAsAdministratorStatusLabel = new Label();
            runAsAdministratorStatusLabel.Dock = DockStyle.Fill;
            runAsAdministratorStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            runAsAdministratorStatusLabel.Font = UiTheme.SectionFont;
            runAsAdministratorStatusLabel.ForeColor = UiTheme.Text;
            runAsAdministratorStatusLabel.BackColor = adminCard.FillColor;
            adminLayout.Controls.Add(runAsAdministratorStatusLabel, 0, 1);

            runAsAdministratorToggle = new ToggleSwitchControl();
            runAsAdministratorToggle.Checked = currentSettings.RunAsAdministrator;
            runAsAdministratorToggle.Anchor = AnchorStyles.Right;
            runAsAdministratorToggle.Margin = new Padding(0);
            runAsAdministratorToggle.CheckedChanged += delegate { UpdateRunAsAdministratorStatus(); };
            adminLayout.Controls.Add(runAsAdministratorToggle, 1, 1);

            Label adminHint = new Label();
            adminHint.Text = "Manual launches may use UAC. With Start with Windows enabled, sign-in startup switches to a highest-privilege task.";
            adminHint.Dock = DockStyle.Fill;
            adminHint.ForeColor = UiTheme.MutedText;
            adminHint.Font = UiTheme.CaptionFont;
            adminHint.BackColor = adminCard.FillColor;
            adminLayout.Controls.Add(adminHint, 0, 2);
            adminLayout.SetColumnSpan(adminHint, 2);

            CardPanel bindingsCard = new CardPanel();
            bindingsCard.Dock = DockStyle.Fill;
            bindingsCard.Padding = new Padding(18, 16, 18, 18);
            bindingsCard.Margin = new Padding(0, 10, 0, 10);
            root.Controls.Add(bindingsCard, 0, 2);

            TableLayoutPanel bindingsLayout = new TableLayoutPanel();
            bindingsLayout.Dock = DockStyle.Fill;
            bindingsLayout.BackColor = UiTheme.Panel;
            bindingsLayout.ColumnCount = 1;
            bindingsLayout.RowCount = 3;
            bindingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            bindingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            bindingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bindingsCard.Controls.Add(bindingsLayout);

            Label bindingsTitle = new Label();
            bindingsTitle.Text = "Active bindings";
            bindingsTitle.Dock = DockStyle.Fill;
            bindingsTitle.TextAlign = ContentAlignment.MiddleLeft;
            bindingsTitle.Font = UiTheme.SectionTitleFont;
            bindingsTitle.ForeColor = UiTheme.Text;
            bindingsTitle.BackColor = bindingsCard.FillColor;
            bindingsLayout.Controls.Add(bindingsTitle, 0, 0);

            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.BackColor = bindingsCard.FillColor;
            header.ColumnCount = 5;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            bindingsLayout.Controls.Add(header, 0, 1);

            AddHeaderLabel(header, "Action", 0);
            AddHeaderLabel(header, "Shortcut", 1);
            AddHeaderLabel(header, string.Empty, 2);
            AddHeaderLabel(header, string.Empty, 3);
            AddHeaderLabel(header, string.Empty, 4);

            rowsScrollHost = new CodexFlowScrollHost();
            rowsScrollHost.Dock = DockStyle.Fill;
            rowsScrollHost.Margin = new Padding(0);
            rowsScrollHost.BackColor = UiTheme.Panel;
            rowsScrollHost.SurfaceColor = UiTheme.Panel;
            bindingsLayout.Controls.Add(rowsScrollHost, 0, 2);

            rowsPanel = rowsScrollHost.Content;
            rowsPanel.Dock = DockStyle.None;
            rowsPanel.BackColor = UiTheme.Panel;
            rowsPanel.Padding = new Padding(0, 6, 0, 0);
            rowsPanel.Resize += delegate { ResizeRows(); };

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = UiTheme.AppBackground;
            footer.ColumnCount = 4;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            root.Controls.Add(footer, 0, 3);

            ThemedButton addButton = new ThemedButton();
            addButton.Text = "Add binding";
            addButton.Dock = DockStyle.Fill;
            UiTheme.ApplyButton(addButton, false);
            addButton.Click += delegate { AddRow(null, null); };
            footer.Controls.Add(addButton, 0, 0);

            ThemedButton cancelButton = new ThemedButton();
            cancelButton.Text = "Cancel";
            cancelButton.Dock = DockStyle.Fill;
            cancelButton.DialogResult = DialogResult.Cancel;
            UiTheme.ApplyButton(cancelButton, false);
            footer.Controls.Add(cancelButton, 2, 0);

            ThemedButton saveButton = new ThemedButton();
            saveButton.Text = "Save";
            saveButton.Dock = DockStyle.Fill;
            UiTheme.ApplyButton(saveButton, true);
            saveButton.Click += delegate { SaveAndClose(); };
            footer.Controls.Add(saveButton, 3, 0);

            AcceptButton = saveButton;
            CancelButton = cancelButton;

            rowsPanel.SuspendLayout();
            for (int i = 0; i < currentSettings.Hotkeys.Count; i++)
            {
                HotkeyBinding binding = currentSettings.Hotkeys[i];
                AddRow(binding.Action, binding.Hotkey);
            }

            if (rows.Count == 0)
            {
                HotkeyDefinition defaultHotkey;
                HotkeyDefinition.TryParse("Ctrl+Alt+F12", out defaultHotkey);
                AddRow(new HotkeyAction { Kind = HotkeyActionKind.Next }, defaultHotkey);
            }
            rowsPanel.ResumeLayout(true);
            ResizeRows();

            UpdateNotificationStatus();
            UpdateStartupStatusLabel();
            UpdateRunAsAdministratorStatus();
            UiTheme.EnableDoubleBufferingRecursive(this);
            ResumeLayout(true);
        }

        public void PrepareForDisplay()
        {
            if (IsDisposed || Width <= 0 || Height <= 0)
            {
                return;
            }

            if (UiTheme.TryWarmUpForm(this))
            {
                UiTheme.PrimeFormBitmap(this);
            }
        }

        private static void AddHeaderLabel(TableLayoutPanel header, string text, int column)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Font = UiTheme.CaptionFont;
            label.ForeColor = UiTheme.MutedText;
            label.BackColor = header.BackColor;
            header.Controls.Add(label, column, 0);
        }

        private static List<HotkeyActionOption> BuildActionOptions(List<AudioDeviceInfo> devices)
        {
            List<HotkeyActionOption> result = new List<HotkeyActionOption>();
            result.Add(new HotkeyActionOption
            {
                DisplayName = "Next output",
                SourceName = "Next",
                Action = new HotkeyAction { Kind = HotkeyActionKind.Next }
            });
            result.Add(new HotkeyActionOption
            {
                DisplayName = "Previous output",
                SourceName = "Previous",
                Action = new HotkeyAction { Kind = HotkeyActionKind.Previous }
            });

            for (int i = 0; i < devices.Count; i++)
            {
                string name = devices[i].Name;
                result.Add(new HotkeyActionOption
                {
                    DisplayName = "Switch to: " + name,
                    SourceName = "Name:" + name,
                    Action = new HotkeyAction { Kind = HotkeyActionKind.DeviceNameContains, NameContains = name }
                });
            }

            return result;
        }

        private void AddRow(HotkeyAction action, HotkeyDefinition hotkey)
        {
            HotkeyRowControl row = new HotkeyRowControl(options, action, hotkey);
            row.Width = Math.Max(640, rowsPanel.ClientSize.Width - 2);
            row.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            row.RemoveRequested += delegate
            {
                rowsPanel.Controls.Remove(row);
                rows.Remove(row);
                row.Dispose();
                ResizeRows();
            };
            rows.Add(row);
            rowsPanel.Controls.Add(row);
            ResizeRows();
        }

        private void SaveAndClose()
        {
            AppSettings next = new AppSettings();
            if (originalSettings != null)
            {
                originalSettings.CopyPreservedSettingsTo(next);
            }
            next.ShowBalloon = showBalloonToggle.Checked;
            next.RunAsAdministrator = runAsAdministratorToggle.Checked;

            HashSet<string> gestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                HotkeyBinding binding;
                string error;
                if (!rows[i].TryBuildBinding(out binding, out error))
                {
                    if (!string.IsNullOrEmpty(error))
                    {
                        MessageBox.Show(this, error, "Hotkey settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    continue;
                }

                string gesture = binding.Hotkey.ToConfigString();
                if (gestures.Contains(gesture))
                {
                    MessageBox.Show(this, "Duplicate input: " + gesture, "Hotkey settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                gestures.Add(gesture);
                next.Hotkeys.Add(binding);
            }

            try
            {
                StartupManager.SetEnabled(startupToggle.Checked, next.RunAsAdministrator);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Settings = next;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateNotificationStatus()
        {
            showBalloonStatusLabel.Text = showBalloonToggle.Checked ? "Enabled" : "Disabled";
            showBalloonStatusLabel.ForeColor = showBalloonToggle.Checked ? UiTheme.Text : UiTheme.MutedText;
        }

        private void UpdateStartupStatusLabel()
        {
            startupStatusLabel.Text = startupToggle.Checked ? "Enabled" : "Disabled";
            startupStatusLabel.ForeColor = startupToggle.Checked ? UiTheme.Text : UiTheme.MutedText;
        }

        private void UpdateRunAsAdministratorStatus()
        {
            bool wantsAdmin = runAsAdministratorToggle.Checked;
            bool elevated = ElevationManager.IsProcessElevated();
            if (wantsAdmin)
            {
                runAsAdministratorStatusLabel.Text = elevated ? "Active" : "Restart required";
                runAsAdministratorStatusLabel.ForeColor = UiTheme.Text;
                return;
            }

            runAsAdministratorStatusLabel.Text = elevated ? "Turns off next launch" : "Disabled";
            runAsAdministratorStatusLabel.ForeColor = elevated ? UiTheme.Text : UiTheme.MutedText;
        }

        private void ResizeRows()
        {
            int width = Math.Max(640, rowsPanel.ClientSize.Width - 2);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Width != width)
                {
                    rows[i].Width = width;
                }
            }
        }
    }

    internal sealed class HotkeyRowControl : UserControl
    {
        private readonly ActionPickerControl actionPicker;
        private readonly Label hotkeyLabel;
        private HotkeyDefinition hotkey;

        public event Action RemoveRequested;

        public HotkeyRowControl(List<HotkeyActionOption> options, HotkeyAction action, HotkeyDefinition initialHotkey)
        {
            Height = 62;
            Margin = new Padding(0, 0, 0, 10);
            Padding = new Padding(0);
            BackColor = UiTheme.Panel;
            ForeColor = UiTheme.Text;
            DoubleBuffered = true;

            CardPanel surface = new CardPanel();
            surface.Dock = DockStyle.Fill;
            surface.CornerRadius = 12;
            surface.FillColor = UiTheme.Panel;
            surface.BorderColor = UiTheme.Border;
            surface.Padding = new Padding(12);
            Controls.Add(surface);

            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.BackColor = UiTheme.Panel;
            table.ColumnCount = 5;
            table.RowCount = 1;
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            surface.Controls.Add(table);

            actionPicker = new ActionPickerControl();
            actionPicker.Dock = DockStyle.Fill;
            actionPicker.CornerRadius = 9;
            actionPicker.Margin = new Padding(0, 2, 10, 2);

            List<HotkeyActionOption> rowOptions = new List<HotkeyActionOption>();
            for (int i = 0; i < options.Count; i++)
            {
                rowOptions.Add(options[i]);
            }

            HotkeyActionOption selected = FindOption(rowOptions, action);
            if (selected == null && action != null)
            {
                selected = new HotkeyActionOption
                {
                    DisplayName = action.ToDisplayName(),
                    SourceName = action.ToSourceName(),
                    Action = action.Clone()
                };
                rowOptions.Add(selected);
            }

            if (selected == null && rowOptions.Count > 0)
            {
                selected = rowOptions[0];
            }
            actionPicker.SetOptions(rowOptions);
            actionPicker.SelectedOption = selected;
            table.Controls.Add(actionPicker, 0, 0);

            CardPanel hotkeyField = new CardPanel();
            hotkeyField.Dock = DockStyle.Fill;
            hotkeyField.CornerRadius = 9;
            hotkeyField.FillColor = UiTheme.PanelElevated;
            hotkeyField.BorderColor = UiTheme.Border;
            hotkeyField.Padding = new Padding(12, 6, 12, 6);
            hotkeyField.Margin = new Padding(10, 0, 10, 0);
            table.Controls.Add(hotkeyField, 1, 0);

            hotkeyLabel = new Label();
            hotkeyLabel.Dock = DockStyle.Fill;
            hotkeyLabel.TextAlign = ContentAlignment.MiddleLeft;
            hotkeyLabel.BorderStyle = BorderStyle.None;
            hotkeyLabel.Padding = new Padding(0, 0, 0, 0);
            hotkeyLabel.BackColor = Color.Transparent;
            hotkeyLabel.Font = UiTheme.UiFontBold;
            hotkeyLabel.AutoEllipsis = true;
            hotkeyField.Controls.Add(hotkeyLabel);

            ThemedButton recordButton = new ThemedButton();
            recordButton.Text = "Record";
            recordButton.CornerRadius = 9;
            recordButton.Dock = DockStyle.Fill;
            recordButton.Margin = new Padding(0, 2, 8, 2);
            UiTheme.ApplyButton(recordButton, true);
            recordButton.Click += delegate { RecordHotkey(); };
            table.Controls.Add(recordButton, 2, 0);

            ThemedButton clearButton = new ThemedButton();
            clearButton.Text = "Clear";
            clearButton.CornerRadius = 9;
            clearButton.Dock = DockStyle.Fill;
            clearButton.Margin = new Padding(0, 2, 8, 2);
            UiTheme.ApplyButton(clearButton, false);
            clearButton.Click += delegate
            {
                hotkey = null;
                UpdateLabel();
            };
            table.Controls.Add(clearButton, 3, 0);

            ThemedButton removeButton = new ThemedButton();
            removeButton.Text = "Remove";
            removeButton.CornerRadius = 9;
            removeButton.Dock = DockStyle.Fill;
            removeButton.Margin = new Padding(0, 2, 0, 2);
            UiTheme.ApplyButton(removeButton, false);
            removeButton.Click += delegate
            {
                Action handler = RemoveRequested;
                if (handler != null)
                {
                    handler();
                }
            };
            table.Controls.Add(removeButton, 4, 0);

            hotkey = initialHotkey == null ? null : initialHotkey.Clone();
            UpdateLabel();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Parent != null)
            {
                int nextWidth = Math.Max(640, Parent.ClientSize.Width - 2);
                if (Width != nextWidth)
                {
                    Width = nextWidth;
                }
            }
        }

        public bool TryBuildBinding(out HotkeyBinding binding, out string error)
        {
            binding = null;
            error = null;

            HotkeyActionOption option = actionPicker.SelectedOption;
            if (option == null)
            {
                if (hotkey != null && !hotkey.IsEmpty)
                {
                    error = "Choose an action for " + hotkey.ToConfigString() + ".";
                    return false;
                }
                return false;
            }

            if (hotkey == null || hotkey.IsEmpty)
            {
                return false;
            }

            binding = new HotkeyBinding
            {
                SourceName = option.SourceName,
                Action = option.Action.Clone(),
                Hotkey = hotkey.Clone()
            };
            return true;
        }

        private void RecordHotkey()
        {
            using (HotkeyCaptureForm capture = new HotkeyCaptureForm())
            {
                capture.PrepareForDisplay();
                UiTheme.AttachRevealOnShown(capture);
                if (capture.ShowDialog(this) == DialogResult.OK)
                {
                    hotkey = capture.Hotkey.Clone();
                    UpdateLabel();
                }
            }
        }

        private void UpdateLabel()
        {
            if (hotkey == null || hotkey.IsEmpty)
            {
                hotkeyLabel.Text = "(not set)";
                hotkeyLabel.ForeColor = UiTheme.MutedText;
            }
            else
            {
                hotkeyLabel.Text = hotkey.ToConfigString();
                hotkeyLabel.ForeColor = UiTheme.Text;
            }
        }

        private static HotkeyActionOption FindOption(List<HotkeyActionOption> options, HotkeyAction action)
        {
            if (action == null)
            {
                return null;
            }
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].Action.SameAs(action))
                {
                    return options[i];
                }
            }
            return null;
        }
    }

    internal sealed class HotkeyCaptureForm : CompositedForm
    {
        private readonly Label displayLabel;
        private readonly Button useButton;
        private readonly HotkeyDefinition current;

        public HotkeyDefinition Hotkey { get; private set; }

        public HotkeyCaptureForm()
        {
            SuspendLayout();
            Text = "Record input";
            Width = 520;
            Height = 320;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            Icon = AppIcon.Load();
            UiTheme.ApplyForm(this);

            current = new HotkeyDefinition();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = UiTheme.AppBackground;
            root.Padding = new Padding(20);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(root);

            Label titleLabel = new Label();
            titleLabel.Text = "Record shortcut";
            titleLabel.Dock = DockStyle.Fill;
            titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            titleLabel.Font = UiTheme.EmphasisFont;
            titleLabel.ForeColor = UiTheme.Text;
            titleLabel.BackColor = Color.Transparent;
            root.Controls.Add(titleLabel, 0, 0);

            CardPanel card = new CardPanel();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(20, 18, 20, 18);
            root.Controls.Add(card, 0, 1);

            TableLayoutPanel cardLayout = new TableLayoutPanel();
            cardLayout.Dock = DockStyle.Fill;
            cardLayout.BackColor = Color.Transparent;
            cardLayout.ColumnCount = 1;
            cardLayout.RowCount = 4;
            cardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            cardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            cardLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            card.Controls.Add(cardLayout);

            Label eyebrowLabel = new Label();
            eyebrowLabel.Text = "INPUT CAPTURE";
            eyebrowLabel.Dock = DockStyle.Fill;
            eyebrowLabel.TextAlign = ContentAlignment.MiddleLeft;
            eyebrowLabel.Font = UiTheme.CaptionFont;
            eyebrowLabel.ForeColor = UiTheme.MutedText;
            eyebrowLabel.BackColor = Color.Transparent;
            cardLayout.Controls.Add(eyebrowLabel, 0, 0);

            Label instructionsLabel = new Label();
            instructionsLabel.Text = "Press a keyboard key, a mouse button, or a keyboard plus mouse combo.";
            instructionsLabel.Dock = DockStyle.Fill;
            instructionsLabel.TextAlign = ContentAlignment.MiddleLeft;
            instructionsLabel.Font = UiTheme.CaptionFont;
            instructionsLabel.ForeColor = UiTheme.MutedText;
            instructionsLabel.BackColor = Color.Transparent;
            cardLayout.Controls.Add(instructionsLabel, 0, 1);

            CardPanel displaySurface = new CardPanel();
            displaySurface.Dock = DockStyle.Fill;
            displaySurface.CornerRadius = 14;
            displaySurface.FillColor = UiTheme.PanelElevated;
            displaySurface.BorderColor = UiTheme.BorderStrong;
            displaySurface.Padding = new Padding(16, 10, 16, 10);
            cardLayout.Controls.Add(displaySurface, 0, 2);

            displayLabel = new Label();
            displayLabel.Dock = DockStyle.Fill;
            displayLabel.TextAlign = ContentAlignment.MiddleCenter;
            displayLabel.Font = UiTheme.EmphasisFont;
            displayLabel.Text = "Press keyboard or mouse input";
            displayLabel.BackColor = Color.Transparent;
            displayLabel.ForeColor = UiTheme.Text;
            displayLabel.AutoEllipsis = true;
            displayLabel.MouseDown += CaptureMouseDown;
            displaySurface.Controls.Add(displayLabel);

            Label footerHintLabel = new Label();
            footerHintLabel.Text = "Esc cancels recording.";
            footerHintLabel.Dock = DockStyle.Fill;
            footerHintLabel.TextAlign = ContentAlignment.MiddleLeft;
            footerHintLabel.Font = UiTheme.CaptionFont;
            footerHintLabel.ForeColor = UiTheme.MutedText;
            footerHintLabel.BackColor = Color.Transparent;
            cardLayout.Controls.Add(footerHintLabel, 0, 3);

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = UiTheme.AppBackground;
            footer.ColumnCount = 3;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            root.Controls.Add(footer, 0, 2);

            useButton = new ThemedButton();
            useButton.Text = "Use";
            useButton.Dock = DockStyle.Fill;
            useButton.Enabled = false;
            UiTheme.ApplyButton(useButton, true);
            useButton.Click += delegate { FinishNow(); };
            footer.Controls.Add(useButton, 1, 0);

            ThemedButton cancelButton = new ThemedButton();
            cancelButton.Text = "Cancel";
            cancelButton.Dock = DockStyle.Fill;
            cancelButton.DialogResult = DialogResult.Cancel;
            UiTheme.ApplyButton(cancelButton, false);
            footer.Controls.Add(cancelButton, 2, 0);

            CancelButton = cancelButton;
            MouseDown += CaptureMouseDown;
            PreviewKeyDown += CapturePreviewKeyDown;
            KeyDown += CaptureKeyDown;
            KeyUp += CaptureKeyUp;

            UiTheme.EnableDoubleBufferingRecursive(this);
            ResumeLayout(true);
        }

        public void PrepareForDisplay()
        {
            if (IsDisposed || Width <= 0 || Height <= 0)
            {
                return;
            }

            if (UiTheme.TryWarmUpForm(this))
            {
                UiTheme.PrimeFormBitmap(this);
            }
        }

        private void CapturePreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            e.IsInputKey = true;
        }

        private void CaptureKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            if (e.Control)
            {
                current.AddKey(Keys.ControlKey);
            }
            if (e.Alt)
            {
                current.AddKey(Keys.Menu);
            }
            if (e.Shift)
            {
                current.AddKey(Keys.ShiftKey);
            }

            Keys key = HotkeyDefinition.NormalizeKey(e.KeyCode);
            current.AddKey(key);
            UpdateDisplay();

            e.SuppressKeyPress = true;
        }

        private void CaptureKeyUp(object sender, KeyEventArgs e)
        {
            Keys key = HotkeyDefinition.NormalizeKey(e.KeyCode);
            if (!HotkeyDefinition.IsModifierKey(key))
            {
                FinishIfReady();
                return;
            }

            UpdateDisplay();
        }

        private void CaptureMouseDown(object sender, MouseEventArgs e)
        {
            MouseInputButton button;
            if (!TryConvertMouseButton(e.Button, out button))
            {
                return;
            }

            AddCurrentModifierKeys();
            current.AddMouseButton(button);
            UpdateDisplay();
            FinishIfReady();
        }

        private static bool TryConvertMouseButton(MouseButtons buttons, out MouseInputButton button)
        {
            button = MouseInputButton.Left;
            if ((buttons & MouseButtons.Left) == MouseButtons.Left)
            {
                button = MouseInputButton.Left;
                return true;
            }
            if ((buttons & MouseButtons.Right) == MouseButtons.Right)
            {
                button = MouseInputButton.Right;
                return true;
            }
            if ((buttons & MouseButtons.Middle) == MouseButtons.Middle)
            {
                button = MouseInputButton.Middle;
                return true;
            }
            if ((buttons & MouseButtons.XButton1) == MouseButtons.XButton1)
            {
                button = MouseInputButton.X1;
                return true;
            }
            if ((buttons & MouseButtons.XButton2) == MouseButtons.XButton2)
            {
                button = MouseInputButton.X2;
                return true;
            }
            return false;
        }

        private void FinishIfReady()
        {
            if (current.IsEmpty)
            {
                return;
            }

            bool hasNonModifierKey = false;
            for (int i = 0; i < current.KeyboardKeys.Count; i++)
            {
                if (!HotkeyDefinition.IsModifierKey(current.KeyboardKeys[i]))
                {
                    hasNonModifierKey = true;
                    break;
                }
            }

            if (!hasNonModifierKey && current.MouseButtons.Count == 0)
            {
                return;
            }

            Hotkey = current.Clone();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void FinishNow()
        {
            if (current.IsEmpty)
            {
                return;
            }

            Hotkey = current.Clone();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateDisplay()
        {
            if (current.IsEmpty)
            {
                displayLabel.Text = "Press keyboard or mouse input";
                useButton.Enabled = false;
            }
            else
            {
                displayLabel.Text = current.ToConfigString();
                useButton.Enabled = true;
            }
        }

        private void AddCurrentModifierKeys()
        {
            Keys modifiers = Control.ModifierKeys;
            if ((modifiers & Keys.Control) == Keys.Control)
            {
                current.AddKey(Keys.ControlKey);
            }
            if ((modifiers & Keys.Alt) == Keys.Alt)
            {
                current.AddKey(Keys.Menu);
            }
            if ((modifiers & Keys.Shift) == Keys.Shift)
            {
                current.AddKey(Keys.ShiftKey);
            }
        }
    }

    internal sealed class PowerRecoveryMonitor : NativeWindow, IDisposable
    {
        private const int WM_POWERBROADCAST = 0x0218;
        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMRESUMECRITICAL = 0x0006;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int WTS_CONSOLE_CONNECT = 0x0001;
        private const int WTS_REMOTE_CONNECT = 0x0003;
        private const int WTS_SESSION_LOGON = 0x0005;
        private const int WTS_SESSION_UNLOCK = 0x0008;
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private const uint WatchdogGapMilliseconds = 6000;

        private readonly FormsTimer watchdogTimer;
        private int lastWatchdogTick;
        private bool disposed;

        public event Action Suspending;
        public event Action Resumed;

        public PowerRecoveryMonitor()
        {
            CreateParams parameters = new CreateParams();
            parameters.Caption = "AudioOutputSwitcher.PowerMonitor";
            parameters.ExStyle = NativeMethods.WS_EX_TOOLWINDOW;
            CreateHandle(parameters);
            NativeMethods.WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION);

            lastWatchdogTick = Environment.TickCount;
            watchdogTimer = new FormsTimer();
            watchdogTimer.Interval = 2000;
            watchdogTimer.Tick += OnWatchdogTick;
            watchdogTimer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST)
            {
                int powerEvent = m.WParam.ToInt32();
                if (powerEvent == PBT_APMSUSPEND)
                {
                    RaiseSuspending();
                }
                else if (powerEvent == PBT_APMRESUMEAUTOMATIC ||
                         powerEvent == PBT_APMRESUMESUSPEND ||
                         powerEvent == PBT_APMRESUMECRITICAL)
                {
                    RaiseResumed();
                }
            }
            else if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int reason = m.WParam.ToInt32();
                if (reason == WTS_SESSION_UNLOCK ||
                    reason == WTS_SESSION_LOGON ||
                    reason == WTS_CONSOLE_CONNECT ||
                    reason == WTS_REMOTE_CONNECT)
                {
                    RaiseResumed();
                }
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            watchdogTimer.Stop();
            watchdogTimer.Dispose();
            if (Handle != IntPtr.Zero)
            {
                NativeMethods.WTSUnRegisterSessionNotification(Handle);
                DestroyHandle();
            }
        }

        private void OnWatchdogTick(object sender, EventArgs e)
        {
            int currentTick = Environment.TickCount;
            uint elapsed = unchecked((uint)(currentTick - lastWatchdogTick));
            lastWatchdogTick = currentTick;
            if (elapsed >= WatchdogGapMilliseconds)
            {
                RaiseResumed();
            }
        }

        private void RaiseSuspending()
        {
            lastWatchdogTick = Environment.TickCount;
            Action handler = Suspending;
            if (handler != null)
            {
                handler();
            }
        }

        private void RaiseResumed()
        {
            lastWatchdogTick = Environment.TickCount;
            Action handler = Resumed;
            if (handler != null)
            {
                handler();
            }
        }
    }

    internal sealed class GlobalHotkeyManager : IDisposable
    {
        private readonly List<HotkeyBinding> bindings = new List<HotkeyBinding>();
        private readonly HashSet<Keys> keysDown = new HashSet<Keys>();
        private readonly HashSet<MouseInputButton> mouseDown = new HashSet<MouseInputButton>();
        private readonly HashSet<string> firedGestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Control dispatcher;
        private readonly NativeMethods.LowLevelProc keyboardProc;
        private readonly NativeMethods.LowLevelProc mouseProc;
        private IntPtr keyboardHook;
        private IntPtr mouseHook;

        public event Action<HotkeyAction> HotkeyPressed;
        public string LastError = string.Empty;

        public GlobalHotkeyManager()
        {
            dispatcher = new Control();
            IntPtr handle = dispatcher.Handle;
            keyboardProc = KeyboardHookCallback;
            mouseProc = MouseHookCallback;
        }

        public bool SetBindings(List<HotkeyBinding> newBindings)
        {
            ClearState();
            bindings.Clear();
            for (int i = 0; i < newBindings.Count; i++)
            {
                HotkeyBinding source = newBindings[i];
                if (source.Action == null || source.Hotkey == null || source.Hotkey.IsEmpty)
                {
                    continue;
                }

                bindings.Add(new HotkeyBinding
                {
                    SourceName = source.SourceName,
                    Action = source.Action.Clone(),
                    Hotkey = source.Hotkey.Clone()
                });
            }

            if (bindings.Count == 0)
            {
                UninstallHooks();
                return true;
            }

            return InstallHooks();
        }

        public void Clear()
        {
            bindings.Clear();
            ClearState();
            UninstallHooks();
        }

        public void ResetInputState()
        {
            ClearState();
        }

        public bool RecoverAfterSystemResume()
        {
            ClearState();
            UninstallHooks();
            if (bindings.Count == 0)
            {
                LastError = string.Empty;
                return true;
            }

            return InstallHooks();
        }

        public void Dispose()
        {
            Clear();
            if (dispatcher != null)
            {
                dispatcher.Dispose();
            }
        }

        private bool InstallHooks()
        {
            LastError = string.Empty;
            IntPtr module = NativeMethods.GetModuleHandle(null);

            if (keyboardHook == IntPtr.Zero)
            {
                keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, keyboardProc, module, 0);
            }
            if (mouseHook == IntPtr.Zero)
            {
                mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, mouseProc, module, 0);
            }

            if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                LastError = "Could not install global keyboard/mouse hooks. Win32 error: " + error.ToString();
                UninstallHooks();
                return false;
            }

            return true;
        }

        private void UninstallHooks()
        {
            if (keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
            }
            if (mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
            }
        }

        private void ClearState()
        {
            keysDown.Clear();
            mouseDown.Clear();
            firedGestures.Clear();
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    NativeMethods.KBDLLHOOKSTRUCT data = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                    Keys key = HotkeyDefinition.NormalizeKey((Keys)data.vkCode);
                    int message = wParam.ToInt32();
                    if (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN)
                    {
                        if (key != Keys.None)
                        {
                            keysDown.Add(key);
                            EvaluateBindings();
                        }
                    }
                    else if (message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP)
                    {
                        if (key != Keys.None)
                        {
                            keysDown.Remove(key);
                            EvaluateBindings();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ClearState();
                CrashLogger.WriteThrottledAsync("Keyboard hook callback failed", ex, TimeSpan.FromMinutes(1));
            }

            return NativeMethods.CallNextHookEx(keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    MouseInputButton button;
                    bool isDown;
                    bool isUp;
                    if (TryGetMouseButton(wParam, lParam, out button, out isDown, out isUp))
                    {
                        if (isDown)
                        {
                            mouseDown.Add(button);
                            EvaluateBindings();
                        }
                        else if (isUp)
                        {
                            mouseDown.Remove(button);
                            EvaluateBindings();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ClearState();
                CrashLogger.WriteThrottledAsync("Mouse hook callback failed", ex, TimeSpan.FromMinutes(1));
            }

            return NativeMethods.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        private bool TryGetMouseButton(IntPtr wParam, IntPtr lParam, out MouseInputButton button, out bool isDown, out bool isUp)
        {
            button = MouseInputButton.Left;
            isDown = false;
            isUp = false;
            int message = wParam.ToInt32();

            if (message == NativeMethods.WM_LBUTTONDOWN || message == NativeMethods.WM_LBUTTONUP)
            {
                button = MouseInputButton.Left;
                isDown = message == NativeMethods.WM_LBUTTONDOWN;
                isUp = message == NativeMethods.WM_LBUTTONUP;
                return true;
            }
            if (message == NativeMethods.WM_RBUTTONDOWN || message == NativeMethods.WM_RBUTTONUP)
            {
                button = MouseInputButton.Right;
                isDown = message == NativeMethods.WM_RBUTTONDOWN;
                isUp = message == NativeMethods.WM_RBUTTONUP;
                return true;
            }
            if (message == NativeMethods.WM_MBUTTONDOWN || message == NativeMethods.WM_MBUTTONUP)
            {
                button = MouseInputButton.Middle;
                isDown = message == NativeMethods.WM_MBUTTONDOWN;
                isUp = message == NativeMethods.WM_MBUTTONUP;
                return true;
            }
            if (message == NativeMethods.WM_XBUTTONDOWN || message == NativeMethods.WM_XBUTTONUP)
            {
                NativeMethods.MSLLHOOKSTRUCT data = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                int xButton = (data.mouseData >> 16) & 0xffff;
                button = xButton == 2 ? MouseInputButton.X2 : MouseInputButton.X1;
                isDown = message == NativeMethods.WM_XBUTTONDOWN;
                isUp = message == NativeMethods.WM_XBUTTONUP;
                return true;
            }

            return false;
        }

        private void EvaluateBindings()
        {
            for (int i = 0; i < bindings.Count; i++)
            {
                HotkeyBinding binding = bindings[i];
                string signature = binding.Hotkey.ToConfigString();
                bool matches = binding.Hotkey.Matches(keysDown, mouseDown);
                if (matches && !firedGestures.Contains(signature))
                {
                    firedGestures.Add(signature);
                    RaiseHotkeyPressed(binding.Action.Clone());
                }
                else if (!matches)
                {
                    firedGestures.Remove(signature);
                }
            }
        }

        private void RaiseHotkeyPressed(HotkeyAction action)
        {
            Action<HotkeyAction> handler = HotkeyPressed;
            if (handler == null)
            {
                return;
            }

            if (dispatcher.IsDisposed)
            {
                return;
            }

            try
            {
                dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        handler(action);
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Write("Hotkey action failed", ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                if (!dispatcher.IsDisposed)
                {
                    CrashLogger.WriteThrottledAsync("Hotkey dispatch failed", ex, TimeSpan.FromMinutes(1));
                }
            }
        }
    }

    internal static class NativeMethods
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_XBUTTONUP = 0x020C;
        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            int crKey,
            ref BLENDFUNCTION pblend,
            int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pvar);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public int mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }
    }

    internal enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    internal enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out int pcDevices);

        [PreserveSig]
        int Item(int nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

        [PreserveSig]
        int GetId(out IntPtr ppstrId);

        [PreserveSig]
        int GetState(out int pdwState);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out int cProps);

        [PreserveSig]
        int GetAt(int iProp, out PROPERTYKEY pkey);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PropVariant pv);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PropVariant propvar);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, out IntPtr ppFormat);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr phnsPeriod);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr pMode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ref PROPERTYKEY key, out PropVariant pv);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ref PROPERTYKEY key, ref PropVariant pv);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    internal interface IPolicyConfigVista
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, out IntPtr ppFormat);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr phnsPeriod);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr pMode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ref PROPERTYKEY key, out PropVariant pv);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ref PROPERTYKEY key, ref PropVariant pv);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    internal static class PropertyKeys
    {
        public static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr PointerValue;

        public string GetString()
        {
            if (VariantType == 31 && PointerValue != IntPtr.Zero)
            {
                return Marshal.PtrToStringUni(PointerValue);
            }
            if (VariantType == 8 && PointerValue != IntPtr.Zero)
            {
                return Marshal.PtrToStringBSTR(PointerValue);
            }
            return string.Empty;
        }
    }
}
