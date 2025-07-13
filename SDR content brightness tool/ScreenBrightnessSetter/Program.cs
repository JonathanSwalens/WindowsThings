using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Security.Principal;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ScreenBrightnessSetter
{
    // Settings for brightness shortcuts and values
    public class ShortcutSettings
    {
        public string IncreaseModifiers { get; set; } = "Control";
        public string IncreaseKey { get; set; } = "F2";
        public string DecreaseModifiers { get; set; } = "Control";
        public string DecreaseKey { get; set; } = "F1";
        public string CustomModifiers { get; set; } = "Control+Shift";
        public string CustomKey { get; set; } = "F3";
        public double CurrentBrightness { get; set; } = 3.6;
        public double StepSize { get; set; } = 0.05;
        public double CustomBrightness { get; set; } = 3.6; // 50% of 1.2 to 6.0 range
    }

    class Program
    {
        // Win32 API imports for brightness control and keyboard hooks
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, int address);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int vKey);

        [DllImport("dwmapi.dll", EntryPoint = "#170")]
        private static extern int DwmGetSDRToHDRBoost(IntPtr monitor, out double brightness);

        private delegate void DwmSetSDRToHDRBoostPtr(IntPtr monitor, double brightness);
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;
        private const string MutexName = "ScreenBrightnessSetter_SingleInstance";

        public const double MaxBrightness = 6.0;
        public const double MinBrightness = 1.2;
        private static double _brightness = 3.6;
        public static double Brightness
        {
            get => _brightness;
            private set => _brightness = Math.Round(Math.Clamp(value, MinBrightness, MaxBrightness), 2);
        }
        private static DwmSetSDRToHDRBoostPtr? _setBrightness;
        private static IntPtr _primaryMonitor;
        private static BrightnessController? _controller;
        private static string _settingsFilePath = string.Empty;
        private static ShortcutSettings _settings = new ShortcutSettings();
        private static IntPtr _keyboardHookID = IntPtr.Zero;
        private static readonly string[] ValidModifiers = ["Control", "Shift", "Control+Shift", "Shift+Alt", "Control+Shift+Alt"];
        private static bool _isCustomBrightnessApplied = false;
        public static bool IsCustomBrightnessApplied
        {
            get => _isCustomBrightnessApplied;
            private set => _isCustomBrightnessApplied = value;
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Ensure single instance using mutex
            using var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("SDR-Content-Brightness-Tool is already running.", 
                    "Application Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                InitializeSettingsPath();
                LoadSettings();

                if (!IsRunningAsAdmin())
                {
                    MessageBox.Show("This program requires administrative privileges.\nPlease run as an administrator.", 
                        "Administrator Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!InitializeBrightnessController())
                {
                    MessageBox.Show("Failed to initialize brightness controller.\nYour system may not support this feature.", 
                        "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                _controller = new BrightnessController();
                Application.Run();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (_keyboardHookID != IntPtr.Zero)
                    UnhookWindowsHookEx(_keyboardHookID);
                _controller?.Dispose();
            }
        }

        // Set the path for saving settings
        private static void InitializeSettingsPath()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                string exeDirectory = Path.GetDirectoryName(exePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _settingsFilePath = Path.Combine(exeDirectory, "brightness_settings.json");
            }
            catch
            {
                _settingsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "brightness_settings.json");
            }
        }

        // Load settings from JSON file
        private static void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var loadedSettings = JsonSerializer.Deserialize<ShortcutSettings>(json);
                    if (loadedSettings != null)
                    {
                        _settings = loadedSettings;
                        Brightness = Math.Round(Math.Clamp(loadedSettings.CurrentBrightness, MinBrightness, MaxBrightness), 2);
                        _settings.StepSize = Math.Clamp(loadedSettings.StepSize, 0.01, 0.5);
                        _settings.CustomBrightness = Math.Round(Math.Clamp(loadedSettings.CustomBrightness, MinBrightness, MaxBrightness), 2);
                    }
                }
                else
                {
                    _settings = new ShortcutSettings();
                    SaveSettings();
                }
            }
            catch
            {
                _settings = new ShortcutSettings();
            }
        }

        // Save settings to JSON file
        public static void SaveSettings()
        {
            try
            {
                _settings.CurrentBrightness = Brightness;
                _settings.CustomBrightness = Math.Round(_settings.CustomBrightness, 2);
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Initialize brightness controller and keyboard hook
        private static bool InitializeBrightnessController()
        {
            try
            {
                _primaryMonitor = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
                if (_primaryMonitor == IntPtr.Zero)
                    return false;

                var dwmapiModule = LoadLibrary("dwmapi.dll");
                if (dwmapiModule == IntPtr.Zero)
                    return false;

                var procAddress = GetProcAddress(dwmapiModule, 171);
                if (procAddress == IntPtr.Zero)
                    return false;

                _setBrightness = Marshal.GetDelegateForFunctionPointer<DwmSetSDRToHDRBoostPtr>(procAddress);
                Brightness = GetCurrentBrightness();
                _setBrightness(_primaryMonitor, Brightness);

                _keyboardHookID = SetKeyboardHook(HookCallback);
                if (_keyboardHookID == IntPtr.Zero)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        // Get current brightness from system
        private static double GetCurrentBrightness()
        {
            try
            {
                if (DwmGetSDRToHDRBoost(_primaryMonitor, out double currentBrightness) == 0)
                    return Math.Round(Math.Clamp(currentBrightness, MinBrightness, MaxBrightness), 2);
                return Brightness;
            }
            catch
            {
                return Brightness;
            }
        }

        // Check if running with admin privileges
        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Set low-level keyboard hook
        private static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
        {
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule!.ModuleName), 0);
        }

        // Convert key states to modifier string
        private static string GetModifierString(bool controlPressed, bool shiftPressed, bool altPressed)
        {
            if (controlPressed && shiftPressed && altPressed)
                return "Control+Shift+Alt";
            if (shiftPressed && altPressed)
                return "Shift+Alt";
            if (controlPressed && shiftPressed)
                return "Control+Shift";
            if (shiftPressed)
                return "Shift";
            if (controlPressed)
                return "Control";
            return "";
        }

        // Handle keyboard hook events
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                var settings = GetSettings();
                bool controlPressed = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shiftPressed = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                bool altPressed = (GetKeyState(VK_MENU) & 0x8000) != 0;

                string modifier = GetModifierString(controlPressed, shiftPressed, altPressed);
                string key = Enum.GetName(typeof(Keys), vkCode) ?? "";

                if (!string.IsNullOrEmpty(modifier) && IsValidKey(key))
                {
                    if (modifier == settings.IncreaseModifiers && key == settings.IncreaseKey)
                        IncreaseBrightness();
                    else if (modifier == settings.DecreaseModifiers && key == settings.DecreaseKey)
                        DecreaseBrightness();
                    else if (modifier == settings.CustomModifiers && key == settings.CustomKey)
                        SetCustomBrightness();
                }
            }
            return CallNextHookEx(_keyboardHookID, nCode, wParam, lParam);
        }

        // Validate keyboard key
        private static bool IsValidKey(string key)
        {
            if (key.StartsWith("F") && int.TryParse(key.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
                return true;
            return new[] { "Up", "Down", "PageUp", "PageDown", "Home", "End", "OemPlus", "OemMinus" }.Contains(key);
        }

        // Calculate percentage from brightness value
        public static double GetPercentage(double brightness)
        {
            return (brightness - MinBrightness) / (MaxBrightness - MinBrightness) * 100;
        }

        // Increase brightness by step size
        public static void IncreaseBrightness()
        {
            try
            {
                double currentBrightness = GetCurrentBrightness();
                double stepPercentage = _settings.StepSize * 100;
                double currentPercentage = GetPercentage(currentBrightness);
                double newPercentage = Math.Min(100, Math.Ceiling(currentPercentage / stepPercentage) * stepPercentage + stepPercentage);
                Brightness = Math.Round(MinBrightness + (newPercentage / 100) * (MaxBrightness - MinBrightness), 2);
                IsCustomBrightnessApplied = false;
                UpdateBrightness(currentBrightness);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error increasing brightness: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Decrease brightness by step size
        public static void DecreaseBrightness()
        {
            try
            {
                double currentBrightness = GetCurrentBrightness();
                double stepPercentage = _settings.StepSize * 100;
                double currentPercentage = GetPercentage(currentBrightness);
                double newPercentage = Math.Max(0, Math.Floor(currentPercentage / stepPercentage) * stepPercentage - stepPercentage);
                Brightness = Math.Round(MinBrightness + (newPercentage / 100) * (MaxBrightness - MinBrightness), 2);
                IsCustomBrightnessApplied = false;
                UpdateBrightness(currentBrightness);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error decreasing brightness: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Set custom brightness value
        public static void SetCustomBrightness()
        {
            try
            {
                double currentBrightness = GetCurrentBrightness();
                Brightness = Math.Round(Math.Clamp(_settings.CustomBrightness, MinBrightness, MaxBrightness), 2);
                IsCustomBrightnessApplied = true;
                UpdateBrightness(currentBrightness);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting custom brightness: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Update system brightness and UI
        private static void UpdateBrightness(double oldBrightness)
        {
            if (Math.Abs(Brightness - oldBrightness) < 0.001)
                return;

            try
            {
                _setBrightness?.Invoke(_primaryMonitor, Brightness);
                _controller?.UpdateBrightnessDisplay(Brightness, IsCustomBrightnessApplied);
                SaveSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating brightness: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Validate hotkey combination
        public static bool ValidateHotkey(string modifiers, string key)
        {
            return ValidModifiers.Contains(modifiers) && IsValidKey(key);
        }

        // Get current settings
        public static ShortcutSettings GetSettings()
        {
            return _settings;
        }

        // Update settings with new values
        public static void UpdateSettings(ShortcutSettings newSettings)
        {
            _settings = newSettings;
            Brightness = Math.Round(Math.Clamp(newSettings.CurrentBrightness, MinBrightness, MaxBrightness), 2);
            _settings.StepSize = Math.Clamp(newSettings.StepSize, 0.01, 0.5);
            _settings.CustomBrightness = Math.Round(Math.Clamp(newSettings.CustomBrightness, MinBrightness, MaxBrightness), 2);
            SaveSettings();
        }
    }

    public class BrightnessController : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _contextMenu;
        private readonly ToolStripMenuItem _brightnessMenuItem;
        private readonly ToolStripMenuItem _settingsMenuItem;
        private readonly ToolStripMenuItem _aboutMenuItem;
        private readonly ToolStripMenuItem _exitMenuItem;
        private readonly ToolStripSeparator _separator;
        private bool _disposed;

        public BrightnessController()
        {
            _notifyIcon = new NotifyIcon();
            _contextMenu = new ContextMenuStrip();
            _brightnessMenuItem = new ToolStripMenuItem("Brightness: 0%") { Enabled = false };
            _separator = new ToolStripSeparator();
            _settingsMenuItem = new ToolStripMenuItem("Settings...");
            _aboutMenuItem = new ToolStripMenuItem("About");
            _exitMenuItem = new ToolStripMenuItem("Exit");

            CreateSystemTrayIcon();
            UpdateBrightnessDisplay(Program.Brightness, false);
        }

        // Create system tray icon and context menu
        private void CreateSystemTrayIcon()
        {
            _settingsMenuItem.Click += ShowSettings;
            _aboutMenuItem.Click += ShowAbout;
            _exitMenuItem.Click += ExitApplication;
            
            _contextMenu.Items.AddRange(new ToolStripItem[] 
            { 
                _brightnessMenuItem, 
                _separator,
                _settingsMenuItem,
                _aboutMenuItem,
                _exitMenuItem 
            });

            _notifyIcon.Icon = CreateBrightnessIcon(Program.Brightness);
            _notifyIcon.Text = GetTooltipText(Program.Brightness, false);
            _notifyIcon.ContextMenuStrip = _contextMenu;
            _notifyIcon.Visible = true;
            
            _notifyIcon.DoubleClick += (sender, e) => 
            {
                var settings = Program.GetSettings();
                double percentage = Program.GetPercentage(Program.Brightness);
                double displayPercentage = Program.IsCustomBrightnessApplied 
                    ? Math.Round(percentage)
                    : settings.StepSize > 0 
                        ? Math.Round(percentage / (settings.StepSize * 100)) * (settings.StepSize * 100)
                        : Math.Round(percentage);
                MessageBox.Show($"Current Brightness: {displayPercentage:F0}%\n\nShortcuts:\n• Increase: {settings.IncreaseModifiers}+{settings.IncreaseKey}\n• Decrease: {settings.DecreaseModifiers}+{settings.DecreaseKey}\n• Custom ({settings.CustomBrightness:F2}x): {settings.CustomModifiers}+{settings.CustomKey}\n\nSettings saved to: brightness_settings.json", 
                    "SDR Content Brightness", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
        }

        // Get rounded percentage for display
        private double GetRoundedPercentage(double brightness, bool isCustomBrightness)
        {
            double percentage = Program.GetPercentage(brightness);
            if (isCustomBrightness)
                return Math.Round(percentage);
            double stepSize = Program.GetSettings().StepSize * 100;
            return stepSize > 0 ? Math.Round(percentage / stepSize) * stepSize : Math.Round(percentage);
        }

        // Get tooltip text for system tray
        private string GetTooltipText(double brightness, bool isCustomBrightness)
        {
            double roundedPercentage = GetRoundedPercentage(brightness, isCustomBrightness);
            return $"Brightness: {roundedPercentage:F0}%";
        }

        // Update brightness display in system tray
        public void UpdateBrightnessDisplay(double brightness, bool isCustomBrightness)
        {
            if (_disposed) return;

            try
            {
                double roundedPercentage = GetRoundedPercentage(brightness, isCustomBrightness);
                _brightnessMenuItem.Text = $"Brightness: {roundedPercentage:F0}%";
                
                var newIcon = CreateBrightnessIcon(brightness);
                if (newIcon != null)
                {
                    _notifyIcon.Icon?.Dispose();
                    _notifyIcon.Icon = newIcon;
                }
                
                _notifyIcon.Text = GetTooltipText(brightness, isCustomBrightness);
            }
            catch
            {
                // Silently handle icon update errors
            }
        }

        // Create system tray icon with brightness percentage
        private Icon CreateBrightnessIcon(double brightness)
        {
            try
            {
                var bitmap = new Bitmap(16, 16);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                
                double roundedPercentage = GetRoundedPercentage(brightness, Program.IsCustomBrightnessApplied);
                Color textColor = roundedPercentage > 70 ? Color.White : roundedPercentage > 30 ? Color.LightGray : Color.LightBlue;
                
                string text = roundedPercentage >= 100 ? "100" : roundedPercentage >= 10 ? $"{(int)roundedPercentage}" : $"{(int)roundedPercentage}";
                
                using var font = new Font("Consolas", 9, FontStyle.Bold);
                using var brush = new SolidBrush(textColor);
                using var blackBrush = new SolidBrush(Color.Black);
                var textSize = g.MeasureString(text, font);
                float x = (16 - textSize.Width) / 2;
                float y = (16 - textSize.Height) / 2;
                
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        if (offsetX != 0 || offsetY != 0)
                            g.DrawString(text, font, blackBrush, x + offsetX, y + offsetY);
                    }
                }
                
                g.DrawString(text, font, brush, x, y);
                
                return Icon.FromHandle(bitmap.GetHicon());
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        // Show settings form
        private void ShowSettings(object? sender, EventArgs e)
        {
            new SettingsForm().ShowDialog();
        }

        // Show about dialog
        private void ShowAbout(object? sender, EventArgs e)
        {
            var settings = Program.GetSettings();
            double percentage = Program.GetPercentage(Program.Brightness);
            double displayPercentage = Program.IsCustomBrightnessApplied 
                ? Math.Round(percentage)
                : settings.StepSize > 0 
                    ? Math.Round(percentage / (settings.StepSize * 100)) * (settings.StepSize * 100)
                    : Math.Round(percentage);
            MessageBox.Show($"SDR Content Brightness\nVersion 1.0.0\n\nAdjusts SDR content brightness using keyboard shortcuts.\n\nCurrent Brightness: {displayPercentage:F0}%\n\nShortcuts:\n• Increase: {settings.IncreaseModifiers}+{settings.IncreaseKey}\n• Decrease: {settings.DecreaseModifiers}+{settings.DecreaseKey}\n• Custom ({settings.CustomBrightness:F2}x): {settings.CustomModifiers}+{settings.CustomKey}\n\nSettings saved to: brightness_settings.json\n\nThanks to @A Hoj for contributions", 
                "About SDR Content Brightness", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Exit application
        private void ExitApplication(object? sender, EventArgs e)
        {
            Program.SaveSettings();
            Application.Exit();
        }

        // Dispose resources
        public void Dispose()
        {
            if (!_disposed)
            {
                _notifyIcon?.Dispose();
                _contextMenu?.Dispose();
                _disposed = true;
            }
        }
    }

    public class SettingsForm : Form
    {
        private readonly ComboBox _increaseModifiersComboBox;
        private readonly ComboBox _increaseKeyComboBox;
        private readonly ComboBox _decreaseModifiersComboBox;
        private readonly ComboBox _decreaseKeyComboBox;
        private readonly ComboBox _customModifiersComboBox;
        private readonly ComboBox _customKeyComboBox;
        private readonly NumericUpDown _stepSizeNumericUpDown;
        private readonly NumericUpDown _customBrightnessNumericUpDown;
        private readonly Button _okButton;
        private readonly Button _cancelButton;
        private readonly Button _defaultsButton;

        public SettingsForm()
        {
            _increaseModifiersComboBox = new ComboBox();
            _increaseKeyComboBox = new ComboBox();
            _decreaseModifiersComboBox = new ComboBox();
            _decreaseKeyComboBox = new ComboBox();
            _customModifiersComboBox = new ComboBox();
            _customKeyComboBox = new ComboBox();
            _stepSizeNumericUpDown = new NumericUpDown();
            _customBrightnessNumericUpDown = new NumericUpDown();
            _okButton = new Button();
            _cancelButton = new Button();
            _defaultsButton = new Button();

            InitializeComponent();
            LoadCurrentSettings();
        }

        // Initialize form controls
        private void InitializeComponent()
        {
            Text = "SDR Content Brightness Settings";
            Size = new Size(450, 400);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var increaseLabel = new Label
            {
                Text = "Increase Brightness:",
                Location = new Point(20, 20),
                Size = new Size(120, 23)
            };

            _increaseModifiersComboBox.Location = new Point(150, 20);
            _increaseModifiersComboBox.Size = new Size(120, 23);
            _increaseModifiersComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _increaseModifiersComboBox.Items.AddRange(new[] { "Control", "Shift", "Control+Shift", "Shift+Alt", "Control+Shift+Alt" });

            _increaseKeyComboBox.Location = new Point(280, 20);
            _increaseKeyComboBox.Size = new Size(80, 23);
            _increaseKeyComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _increaseKeyComboBox.Items.AddRange(GetValidKeys());

            var decreaseLabel = new Label
            {
                Text = "Decrease Brightness:",
                Location = new Point(20, 60),
                Size = new Size(120, 23)
            };

            _decreaseModifiersComboBox.Location = new Point(150, 60);
            _decreaseModifiersComboBox.Size = new Size(120, 23);
            _decreaseModifiersComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _decreaseModifiersComboBox.Items.AddRange(new[] { "Control", "Shift", "Control+Shift", "Shift+Alt", "Control+Shift+Alt" });

            _decreaseKeyComboBox.Location = new Point(280, 60);
            _decreaseKeyComboBox.Size = new Size(80, 23);
            _decreaseKeyComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _decreaseKeyComboBox.Items.AddRange(GetValidKeys());

            var customLabel = new Label
            {
                Text = "Custom Brightness:",
                Location = new Point(20, 100),
                Size = new Size(120, 23)
            };

            _customModifiersComboBox.Location = new Point(150, 100);
            _customModifiersComboBox.Size = new Size(120, 23);
            _customModifiersComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _customModifiersComboBox.Items.AddRange(new[] { "Control", "Shift", "Control+Shift", "Shift+Alt", "Control+Shift+Alt" });

            _customKeyComboBox.Location = new Point(280, 100);
            _customKeyComboBox.Size = new Size(80, 23);
            _customKeyComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _customKeyComboBox.Items.AddRange(GetValidKeys());

            var stepSizeLabel = new Label
            {
                Text = "Step Size (%):",
                Location = new Point(20, 140),
                Size = new Size(120, 23)
            };

            _stepSizeNumericUpDown.Location = new Point(150, 140);
            _stepSizeNumericUpDown.Size = new Size(80, 23);
            _stepSizeNumericUpDown.Minimum = 1;
            _stepSizeNumericUpDown.Maximum = 50;
            _stepSizeNumericUpDown.Increment = 1;
            _stepSizeNumericUpDown.DecimalPlaces = 0;
            _stepSizeNumericUpDown.Value = 5;

            var customBrightnessLabel = new Label
            {
                Text = "Custom Brightness (%):",
                Location = new Point(20, 180),
                Size = new Size(120, 23)
            };

            _customBrightnessNumericUpDown.Location = new Point(150, 180);
            _customBrightnessNumericUpDown.Size = new Size(80, 23);
            _customBrightnessNumericUpDown.Minimum = 0;
            _customBrightnessNumericUpDown.Maximum = 100;
            _customBrightnessNumericUpDown.Increment = 1;
            _customBrightnessNumericUpDown.DecimalPlaces = 0;
            _customBrightnessNumericUpDown.Value = 50;

            var helpLabel = new Label
            {
                Text = "Select a modifier (Control, Shift, Control+Shift, Shift+Alt, Control+Shift+Alt) and a key (F1-F24, Up, Down, PageUp, PageDown, Home, End, +, -).\nSet step size (1-50%) for brightness changes.\nSet custom brightness (0-100%) for instant application.\nHotkeys must be unique.",
                Location = new Point(20, 220),
                Size = new Size(400, 80),
                ForeColor = Color.Gray
            };

            _defaultsButton.Text = "Defaults";
            _defaultsButton.Location = new Point(20, 310);
            _defaultsButton.Size = new Size(80, 30);
            _defaultsButton.Click += DefaultsButton_Click;

            _okButton.Text = "OK";
            _okButton.Location = new Point(260, 310);
            _okButton.Size = new Size(80, 30);
            _okButton.DialogResult = DialogResult.OK;
            _okButton.Click += OkButton_Click;

            _cancelButton.Text = "Cancel";
            _cancelButton.Location = new Point(345, 310);
            _cancelButton.Size = new Size(80, 30);
            _cancelButton.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                increaseLabel, _increaseModifiersComboBox, _increaseKeyComboBox,
                decreaseLabel, _decreaseModifiersComboBox, _decreaseKeyComboBox,
                customLabel, _customModifiersComboBox, _customKeyComboBox,
                stepSizeLabel, _stepSizeNumericUpDown,
                customBrightnessLabel, _customBrightnessNumericUpDown,
                helpLabel, _defaultsButton, _okButton, _cancelButton
            });

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        // Get valid keys for hotkey selection
        private static string[] GetValidKeys()
        {
            var functionKeys = Enumerable.Range(1, 24).Select(i => $"F{i}").ToList();
            var otherKeys = new[] { "Up", "Down", "PageUp", "PageDown", "Home", "End", "OemPlus", "OemMinus" };
            return functionKeys.Concat(otherKeys).ToArray();
        }

        // Load current settings into form
        private void LoadCurrentSettings()
        {
            var settings = Program.GetSettings();
            _increaseModifiersComboBox.SelectedItem = settings.IncreaseModifiers;
            _increaseKeyComboBox.SelectedItem = settings.IncreaseKey;
            _decreaseModifiersComboBox.SelectedItem = settings.DecreaseModifiers;
            _decreaseKeyComboBox.SelectedItem = settings.DecreaseKey;
            _customModifiersComboBox.SelectedItem = settings.CustomModifiers;
            _customKeyComboBox.SelectedItem = settings.CustomKey;
            _stepSizeNumericUpDown.Value = (decimal)(settings.StepSize * 100);
            _customBrightnessNumericUpDown.Value = (decimal)(Program.GetPercentage(settings.CustomBrightness));

            if (_increaseModifiersComboBox.SelectedItem == null || _increaseKeyComboBox.SelectedItem == null)
            {
                _increaseModifiersComboBox.SelectedItem = "Control";
                _increaseKeyComboBox.SelectedItem = "F2";
            }
            if (_decreaseModifiersComboBox.SelectedItem == null || _decreaseKeyComboBox.SelectedItem == null)
            {
                _decreaseModifiersComboBox.SelectedItem = "Control";
                _decreaseKeyComboBox.SelectedItem = "F1";
            }
            if (_customModifiersComboBox.SelectedItem == null || _customKeyComboBox.SelectedItem == null)
            {
                _customModifiersComboBox.SelectedItem = "Control+Shift";
                _customKeyComboBox.SelectedItem = "F3";
            }
            if (_stepSizeNumericUpDown.Value <= 0 || _stepSizeNumericUpDown.Value > 50)
            {
                _stepSizeNumericUpDown.Value = 5;
            }
            if (_customBrightnessNumericUpDown.Value < 0 || _customBrightnessNumericUpDown.Value > 100)
            {
                _customBrightnessNumericUpDown.Value = 50;
            }
        }

        // Reset to default settings
        private void DefaultsButton_Click(object? sender, EventArgs e)
        {
            _increaseModifiersComboBox.SelectedItem = "Control";
            _increaseKeyComboBox.SelectedItem = "F2";
            _decreaseModifiersComboBox.SelectedItem = "Control";
            _decreaseKeyComboBox.SelectedItem = "F1";
            _customModifiersComboBox.SelectedItem = "Control+Shift";
            _customKeyComboBox.SelectedItem = "F3";
            _stepSizeNumericUpDown.Value = 5;
            _customBrightnessNumericUpDown.Value = 50;
        }

        // Save settings and validate
        private void OkButton_Click(object? sender, EventArgs e)
        {
            try
            {
                string increaseModifiers = _increaseModifiersComboBox.SelectedItem?.ToString() ?? "Control";
                string increaseKey = _increaseKeyComboBox.SelectedItem?.ToString() ?? "F2";
                string decreaseModifiers = _decreaseModifiersComboBox.SelectedItem?.ToString() ?? "Control";
                string decreaseKey = _decreaseKeyComboBox.SelectedItem?.ToString() ?? "F1";
                string customModifiers = _customModifiersComboBox.SelectedItem?.ToString() ?? "Control+Shift";
                string customKey = _customKeyComboBox.SelectedItem?.ToString() ?? "F3";
                double stepSize = (double)_stepSizeNumericUpDown.Value / 100;
                double customBrightnessPercentage = (double)_customBrightnessNumericUpDown.Value;
                double customBrightness = Math.Round(Program.MinBrightness + (customBrightnessPercentage / 100) * (Program.MaxBrightness - Program.MinBrightness), 2);

                if (!Program.ValidateHotkey(increaseModifiers, increaseKey))
                {
                    MessageBox.Show($"Invalid Increase hotkey: {increaseModifiers}+{increaseKey}.", 
                        "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!Program.ValidateHotkey(decreaseModifiers, decreaseKey))
                {
                    MessageBox.Show($"Invalid Decrease hotkey: {decreaseModifiers}+{decreaseKey}.", 
                        "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!Program.ValidateHotkey(customModifiers, customKey))
                {
                    MessageBox.Show($"Invalid Custom hotkey: {customModifiers}+{customKey}.", 
                        "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var hotkeys = new[]
                {
                    (increaseModifiers, increaseKey),
                    (decreaseModifiers, decreaseKey),
                    (customModifiers, customKey)
                };

                if (hotkeys.GroupBy(h => (h.Item1, h.Item2)).Any(g => g.Count() > 1))
                {
                    MessageBox.Show("All hotkeys must be unique.", 
                        "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (stepSize <= 0 || stepSize > 0.5)
                {
                    MessageBox.Show("Step size must be between 1% and 50%.", 
                        "Invalid Step Size", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (customBrightnessPercentage < 0 || customBrightnessPercentage > 100)
                {
                    MessageBox.Show("Custom brightness must be between 0% and 100%.", 
                        "Invalid Custom Brightness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var newSettings = new ShortcutSettings
                {
                    IncreaseModifiers = increaseModifiers,
                    IncreaseKey = increaseKey,
                    DecreaseModifiers = decreaseModifiers,
                    DecreaseKey = decreaseKey,
                    CustomModifiers = customModifiers,
                    CustomKey = customKey,
                    CurrentBrightness = Program.Brightness,
                    StepSize = stepSize,
                    CustomBrightness = customBrightness
                };

                Program.UpdateSettings(newSettings);
                MessageBox.Show("Settings saved successfully!", 
                    "SDR Content Brightness", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}", 
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}