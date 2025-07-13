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

namespace ScreenBrightnessSetter
{
    public class ShortcutSettings
    {
        public string IncreaseModifiers { get; set; } = "Control";
        public string IncreaseKey { get; set; } = "F2";
        public string DecreaseModifiers { get; set; } = "Control";
        public string DecreaseKey { get; set; } = "F1";
        public double CurrentBrightness { get; set; } = 3.6;
        public double StepSize { get; set; } = 0.05;
    }

    class Program
    {
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

        private delegate void DwmSetSDRToHDRBoostPtr(IntPtr monitor, double brightness);
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;

        public const double MaxBrightness = 6.0;
        public const double MinBrightness = 1.2;
        private static double _brightness = 3.6;
        public static double Brightness { get => _brightness; private set => _brightness = value; }
        private static DwmSetSDRToHDRBoostPtr? _setBrightness;
        private static IntPtr _primaryMonitor;
        private static BrightnessController? _controller;
        private static string _settingsFilePath = string.Empty;
        private static ShortcutSettings _settings = new ShortcutSettings();
        private static IntPtr _keyboardHookID = IntPtr.Zero;

        private static readonly string[] ValidModifiers = ["Control", "Shift", "Control+Shift", "Shift+Alt", "Control+Shift+Alt"];

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                InitializeSettingsPath();
                LoadSettings();

                if (!IsRunningAsAdmin())
                {
                    MessageBox.Show("This program requires administrative privileges.\nPlease run the program as an administrator.", 
                        "Administrator Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!InitializeBrightnessController())
                {
                    MessageBox.Show("Failed to initialize brightness controller.\nPlease check if your system supports this feature.", 
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
                {
                    UnhookWindowsHookEx(_keyboardHookID);
                    System.Diagnostics.Debug.WriteLine("Main: Keyboard hook unhooked");
                }
                _controller?.Dispose();
            }
        }

        private static void InitializeSettingsPath()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
                string exeDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
                _settingsFilePath = Path.Combine(exeDirectory, "brightness_settings.json");
            }
            catch
            {
                _settingsFilePath = Path.Combine(Environment.CurrentDirectory, "brightness_settings.json");
            }
        }

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
                        Brightness = Math.Clamp(loadedSettings.CurrentBrightness, MinBrightness, MaxBrightness);
                        if (_settings.StepSize <= 0 || _settings.StepSize > 0.5)
                        {
                            _settings.StepSize = 0.05;
                        }
                    }
                }
            }
            catch
            {
                _settings = new ShortcutSettings();
            }
            System.Diagnostics.Debug.WriteLine($"LoadSettings: Increase={_settings.IncreaseModifiers}+{_settings.IncreaseKey}, Decrease={_settings.DecreaseModifiers}+{_settings.DecreaseKey}, StepSize={_settings.StepSize:P0}, Brightness={Brightness:F1}");
        }

        public static void SaveSettings()
        {
            try
            {
                _settings.CurrentBrightness = Brightness;
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
                System.Diagnostics.Debug.WriteLine("SaveSettings: Settings saved");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveSettings: Error: {ex.Message}");
            }
        }

        private static bool InitializeBrightnessController()
        {
            try
            {
                _primaryMonitor = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
                if (_primaryMonitor == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("InitializeBrightnessController: Failed to get primary monitor");
                    return false;
                }

                var dwmapiModule = LoadLibrary("dwmapi.dll");
                if (dwmapiModule == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("InitializeBrightnessController: Failed to load dwmapi.dll");
                    return false;
                }

                var procAddress = GetProcAddress(dwmapiModule, 171);
                if (procAddress == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("InitializeBrightnessController: Failed to get DwmSetSDRToHDRBoost address");
                    return false;
                }

                _setBrightness = Marshal.GetDelegateForFunctionPointer<DwmSetSDRToHDRBoostPtr>(procAddress);
                _setBrightness(_primaryMonitor, Brightness);

                _keyboardHookID = SetKeyboardHook(HookCallback);
                if (_keyboardHookID == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("InitializeBrightnessController: Failed to set keyboard hook");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine("InitializeBrightnessController: Initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitializeBrightnessController: Error: {ex.Message}");
                return false;
            }
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
        {
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule!.ModuleName), 0);
            System.Diagnostics.Debug.WriteLine($"SetKeyboardHook: {(hook == IntPtr.Zero ? "Failed" : "Success")}");
            return hook;
        }

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
                    System.Diagnostics.Debug.WriteLine($"HookCallback: Detected {modifier}+{key}");
                    if (modifier == settings.IncreaseModifiers && key == settings.IncreaseKey)
                    {
                        System.Diagnostics.Debug.WriteLine("HookCallback: Matched Increase hotkey");
                        IncreaseBrightness();
                    }
                    else if (modifier == settings.DecreaseModifiers && key == settings.DecreaseKey)
                    {
                        System.Diagnostics.Debug.WriteLine("HookCallback: Matched Decrease hotkey");
                        DecreaseBrightness();
                    }
                }
            }
            return CallNextHookEx(_keyboardHookID, nCode, wParam, lParam);
        }

        private static bool IsValidKey(string key)
        {
            if (key.StartsWith("F") && int.TryParse(key.Substring(1), out int fNum) && fNum >= 1 && fNum <= 24)
                return true;
            return new[] { "Up", "Down", "PageUp", "PageDown", "Home", "End", "OemPlus", "OemMinus" }.Contains(key);
        }

        public static void IncreaseBrightness()
        {
            double oldBrightness = Brightness;
            double stepPercentage = Math.Round(_settings.StepSize * 100);
            double currentPercentage = ((Brightness - MinBrightness) / (MaxBrightness - MinBrightness)) * 100;
            double newPercentage = Math.Min(100, currentPercentage + stepPercentage);
            Brightness = MinBrightness + (newPercentage / 100) * (MaxBrightness - MinBrightness);
            System.Diagnostics.Debug.WriteLine($"IncreaseBrightness: Old={oldBrightness:F1} ({currentPercentage:F0}%), Step={stepPercentage:F0}%, New={Brightness:F1} ({newPercentage:F0}%)");
            UpdateBrightness(oldBrightness);
        }

        public static void DecreaseBrightness()
        {
            double oldBrightness = Brightness;
            double stepPercentage = Math.Round(_settings.StepSize * 100);
            double currentPercentage = ((Brightness - MinBrightness) / (MaxBrightness - MinBrightness)) * 100;
            double newPercentage = Math.Max(0, currentPercentage - stepPercentage);
            Brightness = MinBrightness + (newPercentage / 100) * (MaxBrightness - MinBrightness);
            System.Diagnostics.Debug.WriteLine($"DecreaseBrightness: Old={oldBrightness:F1} ({currentPercentage:F0}%), Step={stepPercentage:F0}%, New={Brightness:F1} ({newPercentage:F0}%)");
            UpdateBrightness(oldBrightness);
        }

        private static void UpdateBrightness(double oldBrightness)
        {
            if (Math.Abs(Brightness - oldBrightness) > 0.001)
            {
                try
                {
                    Brightness = Math.Clamp(Brightness, MinBrightness, MaxBrightness);
                    System.Diagnostics.Debug.WriteLine($"UpdateBrightness: Clamped brightness={Brightness:F1}");
                    if (_setBrightness != null)
                    {
                        _setBrightness(_primaryMonitor, Brightness);
                        System.Diagnostics.Debug.WriteLine($"UpdateBrightness: Applied brightness={Brightness:F1}");
                        _controller?.UpdateBrightnessDisplay(Brightness);
                        SaveSettings();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UpdateBrightness: Error: {ex.Message}");
                }
            }
        }

        public static bool ValidateHotkey(string modifiers, string key)
        {
            bool isValid = ValidModifiers.Contains(modifiers) && IsValidKey(key);
            System.Diagnostics.Debug.WriteLine($"ValidateHotkey: Modifiers={modifiers}, Key={key}, Valid={isValid}");
            return isValid;
        }

        public static ShortcutSettings GetSettings()
        {
            return _settings;
        }

        public static void UpdateSettings(ShortcutSettings newSettings)
        {
            _settings = newSettings;
            Brightness = Math.Clamp(newSettings.CurrentBrightness, MinBrightness, MaxBrightness);
            System.Diagnostics.Debug.WriteLine($"UpdateSettings: Increase={newSettings.IncreaseModifiers}+{newSettings.IncreaseKey}, Decrease={newSettings.DecreaseModifiers}+{newSettings.DecreaseKey}, StepSize={newSettings.StepSize:P0}, Brightness={Brightness:F1}");
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
            UpdateBrightnessDisplay(Program.Brightness);
        }

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
            _notifyIcon.Text = GetTooltipText(Program.Brightness);
            _notifyIcon.ContextMenuStrip = _contextMenu;
            _notifyIcon.Visible = true;
            
            _notifyIcon.DoubleClick += (sender, e) => 
            {
                double percentage = ((Program.Brightness - Program.MinBrightness) / (Program.MaxBrightness - Program.MinBrightness)) * 100;
                var settings = Program.GetSettings();
                MessageBox.Show($"Current Brightness: {percentage:F0}%\n\nShortcuts:\n• Increase: {settings.IncreaseModifiers}+{settings.IncreaseKey}\n• Decrease: {settings.DecreaseModifiers}+{settings.DecreaseKey}\n\nSettings saved to: brightness_settings.json", 
                    "SDR Content Brightness", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
        }

        private string GetTooltipText(double brightness)
        {
            double percentage = ((brightness - Program.MinBrightness) / (Program.MaxBrightness - Program.MinBrightness)) * 100;
            return $"Brightness: {percentage:F0}%";
        }

        public void UpdateBrightnessDisplay(double brightness)
        {
            if (_disposed) return;

            try
            {
                double percentage = ((brightness - Program.MinBrightness) / (Program.MaxBrightness - Program.MinBrightness)) * 100;
                _brightnessMenuItem.Text = $"Brightness: {percentage:F0}%";
                
                var newIcon = CreateBrightnessIcon(brightness);
                if (newIcon != null)
                {
                    _notifyIcon.Icon?.Dispose();
                    _notifyIcon.Icon = newIcon;
                }
                
                _notifyIcon.Text = GetTooltipText(brightness);
            }
            catch
            {
            }
        }

        private Icon CreateBrightnessIcon(double brightness)
        {
            try
            {
                var bitmap = new Bitmap(16, 16);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                
                double percentage = ((brightness - Program.MinBrightness) / (Program.MaxBrightness - Program.MinBrightness)) * 100;
                Color textColor = percentage > 70 ? Color.White : percentage > 30 ? Color.LightGray : Color.LightBlue;
                
                string text = percentage >= 100 ? "100" : percentage >= 10 ? $"{(int)percentage}" : $"{(int)percentage}";
                
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
                        {
                            g.DrawString(text, font, blackBrush, x + offsetX, y + offsetY);
                        }
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

        private void ShowSettings(object? sender, EventArgs e)
        {
            new SettingsForm().ShowDialog();
        }

        private void ShowAbout(object? sender, EventArgs e)
        {
            var settings = Program.GetSettings();
            MessageBox.Show($"SDR Content Brightness\n\nAdjusts SDR content brightness using keyboard shortcuts.\n\nShortcuts:\n• Increase: {settings.IncreaseModifiers}+{settings.IncreaseKey}\n• Decrease: {settings.DecreaseModifiers}+{settings.DecreaseKey}\n\nSettings saved to: brightness_settings.json", 
                "About SDR Content Brightness", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExitApplication(object? sender, EventArgs e)
        {
            Program.SaveSettings();
            Application.Exit();
        }

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
        private readonly NumericUpDown _stepSizeNumericUpDown;
        private readonly Button _okButton;
        private readonly Button _cancelButton;
        private readonly Button _defaultsButton;

        public SettingsForm()
        {
            _increaseModifiersComboBox = new ComboBox();
            _increaseKeyComboBox = new ComboBox();
            _decreaseModifiersComboBox = new ComboBox();
            _decreaseKeyComboBox = new ComboBox();
            _stepSizeNumericUpDown = new NumericUpDown();
            _okButton = new Button();
            _cancelButton = new Button();
            _defaultsButton = new Button();

            InitializeComponent();
            LoadCurrentSettings();
        }

        private void InitializeComponent()
        {
            Text = "SDR Content Brightness Settings";
            Size = new Size(450, 350);
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

            var stepSizeLabel = new Label
            {
                Text = "Step Size (%):",
                Location = new Point(20, 100),
                Size = new Size(120, 23)
            };

            _stepSizeNumericUpDown.Location = new Point(150, 100);
            _stepSizeNumericUpDown.Size = new Size(80, 23);
            _stepSizeNumericUpDown.Minimum = 1;
            _stepSizeNumericUpDown.Maximum = 50;
            _stepSizeNumericUpDown.Increment = 1;
            _stepSizeNumericUpDown.DecimalPlaces = 0;
            _stepSizeNumericUpDown.Value = 5;

            var helpLabel = new Label
            {
                Text = "Select a modifier (Control, Shift, Control+Shift, Shift+Alt, Control+Shift+Alt) and a key (F1-F24, Up, Down, PageUp, PageDown, Home, End, +, -).\nSet step size (1-50%) for brightness changes.\nHotkeys must be unique.",
                Location = new Point(20, 140),
                Size = new Size(400, 80),
                ForeColor = Color.Gray
            };

            _defaultsButton.Text = "Defaults";
            _defaultsButton.Location = new Point(20, 240);
            _defaultsButton.Size = new Size(80, 30);
            _defaultsButton.Click += DefaultsButton_Click;

            _okButton.Text = "OK";
            _okButton.Location = new Point(260, 240);
            _okButton.Size = new Size(80, 30);
            _okButton.DialogResult = DialogResult.OK;
            _okButton.Click += OkButton_Click;

            _cancelButton.Text = "Cancel";
            _cancelButton.Location = new Point(345, 240);
            _cancelButton.Size = new Size(80, 30);
            _cancelButton.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[]
            {
                increaseLabel, _increaseModifiersComboBox, _increaseKeyComboBox,
                decreaseLabel, _decreaseModifiersComboBox, _decreaseKeyComboBox,
                stepSizeLabel, _stepSizeNumericUpDown,
                helpLabel, _defaultsButton, _okButton, _cancelButton
            });

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private static string[] GetValidKeys()
        {
            var functionKeys = Enumerable.Range(1, 24).Select(i => $"F{i}").ToList();
            var otherKeys = new[] { "Up", "Down", "PageUp", "PageDown", "Home", "End", "OemPlus", "OemMinus" };
            return functionKeys.Concat(otherKeys).ToArray();
        }

        private void LoadCurrentSettings()
        {
            var settings = Program.GetSettings();
            _increaseModifiersComboBox.SelectedItem = settings.IncreaseModifiers;
            _increaseKeyComboBox.SelectedItem = settings.IncreaseKey;
            _decreaseModifiersComboBox.SelectedItem = settings.DecreaseModifiers;
            _decreaseKeyComboBox.SelectedItem = settings.DecreaseKey;
            _stepSizeNumericUpDown.Value = (decimal)(settings.StepSize * 100);

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
            if (_stepSizeNumericUpDown.Value <= 0 || _stepSizeNumericUpDown.Value > 50)
            {
                _stepSizeNumericUpDown.Value = 5;
            }
        }

        private void DefaultsButton_Click(object? sender, EventArgs e)
        {
            _increaseModifiersComboBox.SelectedItem = "Control";
            _increaseKeyComboBox.SelectedItem = "F2";
            _decreaseModifiersComboBox.SelectedItem = "Control";
            _decreaseKeyComboBox.SelectedItem = "F1";
            _stepSizeNumericUpDown.Value = 5;
        }

        private void OkButton_Click(object? sender, EventArgs e)
        {
            try
            {
                string increaseModifiers = _increaseModifiersComboBox.SelectedItem?.ToString() ?? "Control";
                string increaseKey = _increaseKeyComboBox.SelectedItem?.ToString() ?? "F2";
                string decreaseModifiers = _decreaseModifiersComboBox.SelectedItem?.ToString() ?? "Control";
                string decreaseKey = _decreaseKeyComboBox.SelectedItem?.ToString() ?? "F1";
                double stepSize = (double)_stepSizeNumericUpDown.Value / 100;

                System.Diagnostics.Debug.WriteLine($"OkButton_Click: Increase={increaseModifiers}+{increaseKey}, Decrease={decreaseModifiers}+{decreaseKey}, StepSize={stepSize:P0}");

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

                if (increaseModifiers == decreaseModifiers && increaseKey == decreaseKey)
                {
                    MessageBox.Show("Increase and Decrease hotkeys cannot be the same.", 
                        "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (stepSize <= 0 || stepSize > 0.5)
                {
                    MessageBox.Show("Step size must be between 1% and 50%.", 
                        "Invalid Step Size", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var newSettings = new ShortcutSettings
                {
                    IncreaseModifiers = increaseModifiers,
                    IncreaseKey = increaseKey,
                    DecreaseModifiers = decreaseModifiers,
                    DecreaseKey = decreaseKey,
                    CurrentBrightness = Program.Brightness,
                    StepSize = stepSize
                };

                Program.UpdateSettings(newSettings);
                MessageBox.Show("Settings saved successfully!", 
                    "SDR Content Brightness", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OkButton_Click: Error: {ex.Message}");
                MessageBox.Show($"Error saving settings: {ex.Message}", 
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}