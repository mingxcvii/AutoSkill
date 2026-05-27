using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClickableTransparentOverlay;
using ImGuiNET;

namespace AutoSkill
{
    // Class representing a skill config
    public class Skill
    {
        public bool Enabled = false;
        public string Key = "";
        public float Cooldown = 0.0f;
        [JsonIgnore]
        public DateTime LastUsed = DateTime.MinValue;
    }

    public class PresetConfig
    {
        public string Name { get; set; } = "Default";
        public string ToggleKeyStr { get; set; } = "HOME";
        public string SwitchWindowKeyStr { get; set; } = "";
        public string TargetWindowName { get; set; } = "";
        
        [JsonIgnore]
        public bool IsMacroRunning { get; set; } = false;

        public List<Skill> Skills { get; set; } = new List<Skill>();
    }

    public class Renderer : Overlay
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private bool MacroRunning 
        {
            get 
            {
                if (Presets.Count == 0) return false;
                return Presets[CurrentPresetIndex].IsMacroRunning;
            }
            set 
            {
                if (Presets.Count > 0)
                {
                    Presets[CurrentPresetIndex].IsMacroRunning = value;
                }
            }
        }
        
        public List<PresetConfig> Presets = new List<PresetConfig>();
        public int CurrentPresetIndex = 0;
        private string _newPresetName = "";
        private bool _showNewPresetPopup = false;
        
        public List<Skill> Skills => Presets.Count > 0 ? Presets[CurrentPresetIndex].Skills : new List<Skill>();
        public string ToggleKeyStr 
        {
            get => Presets.Count > 0 ? Presets[CurrentPresetIndex].ToggleKeyStr : "HOME";
            set { if (Presets.Count > 0) Presets[CurrentPresetIndex].ToggleKeyStr = value; }
        }
        public string SwitchWindowKeyStr 
        {
            get => Presets.Count > 0 ? Presets[CurrentPresetIndex].SwitchWindowKeyStr : "";
            set { if (Presets.Count > 0) Presets[CurrentPresetIndex].SwitchWindowKeyStr = value; }
        }

        private bool _styleInitialized = false;
        private bool _sizeSet = false;
        private volatile bool _showMenu = true;
        private IntPtr _overlayHandle = IntPtr.Zero;

        private int _bindingIndex = -1; // -1: none, 0: toggle key, -2: switch window key, 1+: skills
        private DateTime _bindStartTime;

        private List<string> _availableWindows = new List<string>() { "[NONE - SELECT A GAME WINDOW]" };
        private DateTime _lastWindowRefresh = DateTime.MinValue;

        // Win32 API Imports for global keyboard control
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        
        public static string GlobalHideKey = "INSERT";
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);


        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);


        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

        private const int SW_RESTORE = 9;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const int KEYEVENTF_SCANCODE = 0x0008;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private void ActivateTargetWindow()
        {
            if (Presets.Count == 0) return;
            string targetStr = Presets[CurrentPresetIndex].TargetWindowName;
            
            if (string.IsNullOrEmpty(targetStr) || targetStr == "[NONE - SELECT A GAME WINDOW]") return;

            IntPtr hWnd = GetTargetWindowHandle();
            if (hWnd != IntPtr.Zero)
            {
                ForceForegroundWindow(hWnd);
            }
        }

        private string GetActiveWindowTitle()
        {
            const int nChars = 256;
            StringBuilder Buff = new StringBuilder(nChars);
            IntPtr handle = GetForegroundWindow();
            if (GetWindowText(handle, Buff, nChars) > 0)
            {
                return Buff.ToString();
            }
            return string.Empty;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const int SW_MINIMIZE = 6;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_RESTORE = 0xF120;

        private void ForceForegroundWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            
            // Proper way to restore a window that hooks OS messages (like games)
            // This is identical to clicking the icon in the taskbar
            SendMessage(hWnd, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero);
            
            Thread.Sleep(50);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }

        private bool IsPresetWindowActive(PresetConfig preset)
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            return hwnd == GetTargetWindowHandleForPreset(preset);
        }

        private bool IsTargetWindowActive()
        {
            if (Presets.Count == 0) return false;
            string targetStr = Presets[CurrentPresetIndex].TargetWindowName;
            if (string.IsNullOrEmpty(targetStr) || targetStr == "[NONE - SELECT A GAME WINDOW]") return false;

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            // 1. Try PID match
            int bracketIndex = targetStr.IndexOf(']');
            if (bracketIndex > 1 && targetStr.StartsWith("["))
            {
                if (int.TryParse(targetStr.Substring(1, bracketIndex - 1), out int targetPid))
                {
                    GetWindowThreadProcessId(hwnd, out uint activePid);
                    if (activePid == targetPid) return true;
                }
            }

            // 2. Fallback: match by window title
            string activeTitle = GetActiveWindowTitle();
            string savedTitle = ExtractTitleFromTarget(targetStr);
            if (!string.IsNullOrEmpty(savedTitle) && activeTitle == savedTitle)
            {
                GetWindowThreadProcessId(hwnd, out uint newPid);
                
                // Ensure this new PID isn't already claimed by another preset!
                for (int i = 0; i < Presets.Count; i++)
                {
                    if (i == CurrentPresetIndex) continue;
                    string otherTarget = Presets[i].TargetWindowName;
                    if (!string.IsNullOrEmpty(otherTarget))
                    {
                        int bi = otherTarget.IndexOf(']');
                        if (bi > 1 && otherTarget.StartsWith("["))
                        {
                            if (uint.TryParse(otherTarget.Substring(1, bi - 1), out uint otherPid))
                            {
                                if (otherPid == newPid) return false; // Another preset owns this window, don't steal it
                            }
                        }
                    }
                }

                // Auto-update PID
                Presets[CurrentPresetIndex].TargetWindowName = $"[{newPid}] {savedTitle}";
                return true;
            }
            return false;
        }

        private string ExtractTitleFromTarget(string targetStr)
        {
            if (string.IsNullOrEmpty(targetStr)) return "";
            int bracketEnd = targetStr.IndexOf(']');
            if (bracketEnd >= 0 && bracketEnd + 2 < targetStr.Length)
                return targetStr.Substring(bracketEnd + 2); // Skip "] "
            return targetStr;
        }

        private IntPtr GetTargetWindowHandle()
        {
            if (Presets.Count == 0) return IntPtr.Zero;
            return GetTargetWindowHandleForPreset(Presets[CurrentPresetIndex]);
        }

        private IntPtr GetTargetWindowHandleForPreset(PresetConfig preset)
        {
            string targetStr = preset.TargetWindowName;
            if (string.IsNullOrEmpty(targetStr) || targetStr == "[NONE - SELECT A GAME WINDOW]") return IntPtr.Zero;

            int bracketIndex = targetStr.IndexOf(']');
            if (bracketIndex > 1 && targetStr.StartsWith("["))
            {
                if (int.TryParse(targetStr.Substring(1, bracketIndex - 1), out int pid))
                {
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                            return p.MainWindowHandle;
                    }
                    catch { }
                }
            }

            // 2. Fallback: match by window title (handles PID changes after restart)
            string savedTitle = ExtractTitleFromTarget(targetStr);
            if (!string.IsNullOrEmpty(savedTitle))
            {
                // Collect PIDs already claimed by OTHER presets
                var claimedPids = new HashSet<int>();
                for (int i = 0; i < Presets.Count; i++)
                {
                    if (i == CurrentPresetIndex) continue;
                    string otherTarget = Presets[i].TargetWindowName;
                    if (!string.IsNullOrEmpty(otherTarget))
                    {
                        int bi = otherTarget.IndexOf(']');
                        if (bi > 1 && otherTarget.StartsWith("["))
                        {
                            if (int.TryParse(otherTarget.Substring(1, bi - 1), out int otherPid))
                            {
                                try { if (Process.GetProcessById(otherPid).MainWindowHandle != IntPtr.Zero) claimedPids.Add(otherPid); } catch { }
                            }
                        }
                    }
                }

                var match = Process.GetProcesses()
                    .FirstOrDefault(p => p.MainWindowTitle == savedTitle 
                        && p.MainWindowHandle != IntPtr.Zero 
                        && !claimedPids.Contains(p.Id));
                if (match != null)
                {
                    // Auto-update the saved TargetWindowName with new PID
                    string newTarget = $"[{match.Id}] {match.MainWindowTitle}";
                    preset.TargetWindowName = newTarget;
                    SaveSettings();
                    return match.MainWindowHandle;
                }
            }
            return IntPtr.Zero;
        }

        private void RefreshWindowList()
        {
            if (MacroRunning) return; // Do not refresh and risk resetting while macro is active

            if ((DateTime.Now - _lastWindowRefresh).TotalSeconds < 2) return;
            _lastWindowRefresh = DateTime.Now;

            var currentList = Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                .Select(p => $"[{p.Id}] {p.MainWindowTitle}")
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            currentList.Insert(0, "[NONE - SELECT A GAME WINDOW]");
            _availableWindows = currentList;
        }

        private void SaveSettings()
        {
            try
            {
                var options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
                string json = JsonSerializer.Serialize(Presets, options);
                File.WriteAllText("presets.json", json);
                File.WriteAllText("last_preset.txt", CurrentPresetIndex.ToString());
                File.WriteAllText("hidekey.txt", GlobalHideKey);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSkill] SaveSettings failed: {ex.Message}");
            }
        }

        private List<Skill> CreateDefaultSkills()
        {
            var list = new List<Skill>();
            string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10" };
            foreach (var k in keys) list.Add(new Skill { Key = k });
            return list;
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists("presets.json"))
                {
                    string json = File.ReadAllText("presets.json");
                    var options = new JsonSerializerOptions { IncludeFields = true };
                    var loaded = JsonSerializer.Deserialize<List<PresetConfig>>(json, options);
                    if (loaded != null && loaded.Count > 0)
                    {
                        Presets = loaded;
                        // Ensure all presets have 20 skills
                        foreach (var p in Presets)
                        {
                            while (p.Skills.Count < 20) p.Skills.Add(new Skill());
                        }

                        if (File.Exists("last_preset.txt"))
                        {
                            if (int.TryParse(File.ReadAllText("last_preset.txt"), out int lastIdx))
                            {
                                if (lastIdx >= 0 && lastIdx < Presets.Count)
                                {
                                    CurrentPresetIndex = lastIdx;
                                }
                            }
                        }
                        if (File.Exists("hidekey.txt"))
                        {
                            GlobalHideKey = File.ReadAllText("hidekey.txt").Trim();
                        }
                        return;
                    }
                }
                
                // Fallback to legacy config
                var legacyPreset = new PresetConfig { Name = "Default", Skills = CreateDefaultSkills() };
                if (File.Exists("config.json"))
                {
                    string json = File.ReadAllText("config.json");
                    var loaded = JsonSerializer.Deserialize<List<Skill>>(json, new JsonSerializerOptions { IncludeFields = true });
                    if (loaded != null)
                    {
                        int count = Math.Min(loaded.Count, legacyPreset.Skills.Count);
                        for (int i = 0; i < count; i++)
                        {
                            legacyPreset.Skills[i].Enabled = loaded[i].Enabled;
                            legacyPreset.Skills[i].Key = loaded[i].Key;
                            legacyPreset.Skills[i].Cooldown = loaded[i].Cooldown;
                        }
                    }
                }
                if (File.Exists("hotkey.txt")) legacyPreset.ToggleKeyStr = File.ReadAllText("hotkey.txt").Trim();
                
                Presets.Add(legacyPreset);
                SaveSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSkill] LoadSettings failed: {ex.Message}");
                if (Presets.Count == 0) Presets.Add(new PresetConfig { Name = "Default", Skills = CreateDefaultSkills() });
            }
        }

        private void AutoRematchWindows()
        {
            // On startup, try to re-match each preset's target window by title
            for (int i = 0; i < Presets.Count; i++)
            {
                var preset = Presets[i];
                if (string.IsNullOrEmpty(preset.TargetWindowName) || preset.TargetWindowName == "[NONE - SELECT A GAME WINDOW]")
                    continue;

                string savedTitle = ExtractTitleFromTarget(preset.TargetWindowName);
                if (string.IsNullOrEmpty(savedTitle)) continue;

                // Check if saved PID still valid
                int bracketIndex = preset.TargetWindowName.IndexOf(']');
                if (bracketIndex > 1 && preset.TargetWindowName.StartsWith("["))
                {
                    if (int.TryParse(preset.TargetWindowName.Substring(1, bracketIndex - 1), out int pid))
                    {
                        try
                        {
                            var p = Process.GetProcessById(pid);
                            if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle == savedTitle)
                                continue; // PID still valid, no need to rematch
                        }
                        catch { }
                    }
                }

                // PID invalid, find by title (excluding PIDs claimed by other presets)
                var claimedPids = new HashSet<int>();
                for (int j = 0; j < Presets.Count; j++)
                {
                    if (j == i) continue;
                    string otherTarget = Presets[j].TargetWindowName;
                    if (!string.IsNullOrEmpty(otherTarget))
                    {
                        int bi = otherTarget.IndexOf(']');
                        if (bi > 1 && otherTarget.StartsWith("["))
                        {
                            if (int.TryParse(otherTarget.Substring(1, bi - 1), out int otherPid))
                                claimedPids.Add(otherPid);
                        }
                    }
                }

                var match = Process.GetProcesses()
                    .FirstOrDefault(p => p.MainWindowTitle == savedTitle
                        && p.MainWindowHandle != IntPtr.Zero
                        && !claimedPids.Contains(p.Id));
                if (match != null)
                {
                    preset.TargetWindowName = $"[{match.Id}] {match.MainWindowTitle}";
                }
            }
            SaveSettings();
        }

        public Renderer() : base("Auto Skill")
        {
            // Disable Windows foreground lock so we can switch windows freely
            SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, 0);

            LoadSettings();
            AutoRematchWindows(); // Auto-find game windows by title on startup

            // Separate thread for hotkeys to prevent polling delay
            Thread hotkeyThread = new Thread(HotkeyLoop);
            hotkeyThread.IsBackground = true;
            hotkeyThread.Start();

            // Background thread for auto-casting
            Thread macroThread = new Thread(MacroLoop);
            macroThread.IsBackground = true;
            macroThread.Start();
        }

        private void InitializeStyle()
        {
            if (_styleInitialized) return;
            _styleInitialized = true;

            var style = ImGui.GetStyle();
            
            // Clean ImGui classic dark styling
            style.WindowRounding = 0f;
            style.FrameRounding = 2f;
            style.GrabRounding = 2f;
            style.PopupRounding = 0f;
            style.ScrollbarRounding = 0f;
            style.ChildRounding = 0f;
            style.TabRounding = 2f;
            style.WindowBorderSize = 1f;
            style.FrameBorderSize = 1f;
            style.ChildBorderSize = 1f;
            style.WindowPadding = new Vector2(8, 8);
            style.FramePadding = new Vector2(4, 3);
            style.ItemSpacing = new Vector2(8, 4);

            // Clean Dark Theme with Blue accents
            style.Colors[(int)ImGuiCol.WindowBg] = new Vector4(0.08f, 0.08f, 0.08f, 0.94f);
            style.Colors[(int)ImGuiCol.Border] = new Vector4(0.30f, 0.30f, 0.30f, 0.50f);
            style.Colors[(int)ImGuiCol.TitleBg] = new Vector4(0.15f, 0.25f, 0.40f, 1.00f);
            style.Colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.20f, 0.35f, 0.55f, 1.00f);
            
            style.Colors[(int)ImGuiCol.Button] = new Vector4(0.20f, 0.35f, 0.55f, 0.80f);
            style.Colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.30f, 0.45f, 0.65f, 1.00f);
            style.Colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.40f, 0.55f, 0.75f, 1.00f);
            
            style.Colors[(int)ImGuiCol.FrameBg] = new Vector4(0.12f, 0.12f, 0.12f, 1.00f);
            style.Colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.20f, 0.35f, 0.55f, 0.80f);
            style.Colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.20f, 0.35f, 0.55f, 1.00f);

            style.Colors[(int)ImGuiCol.CheckMark] = new Vector4(0.30f, 0.60f, 0.90f, 1.00f);
            
            style.Colors[(int)ImGuiCol.Header] = new Vector4(0.20f, 0.35f, 0.55f, 0.80f);
            style.Colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.30f, 0.45f, 0.65f, 1.00f);
            style.Colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.40f, 0.55f, 0.75f, 1.00f);
            
            style.Colors[(int)ImGuiCol.Text] = new Vector4(0.95f, 0.95f, 0.95f, 1.00f);

            // Table & Scrollbar styling
            style.CellPadding = new Vector2(4, 4);
            style.Colors[(int)ImGuiCol.TableHeaderBg] = new Vector4(0.15f, 0.25f, 0.40f, 1.00f);
            style.Colors[(int)ImGuiCol.TableRowBg] = new Vector4(0.08f, 0.08f, 0.08f, 0.95f);
            style.Colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(0.08f, 0.08f, 0.08f, 0.95f);
        }

        private static byte GetVirtualKeyCode(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return 0;
            
            string cleanKeyName = keyName.Trim().ToUpper();

            // 1. Check special keys
            switch (cleanKeyName)
            {
                case "SPACE": return 0x20;
                case "ENTER": return 0x0D;
                case "TAB": return 0x09;
                case "ESCAPE": return 0x1B;
                case "ESC": return 0x1B;
                case "SHIFT": return 0x10;
                case "LSHIFT": return 0xA0;
                case "RSHIFT": return 0xA1;
                case "CTRL": return 0x11;
                case "CONTROL": return 0x11;
                case "LCTRL": return 0xA2;
                case "RCTRL": return 0xA3;
                case "ALT": return 0x12;
                case "MENU": return 0x12;
                case "BACKSPACE": return 0x08;
                case "BACK": return 0x08;
                case "CAPSLOCK": return 0x14;
                case "CAPS": return 0x14;
                case "PAGEUP": return 0x21;
                case "PAGEDOWN": return 0x22;
                case "END": return 0x23;
                case "HOME": return 0x24;
                case "LEFT": return 0x25;
                case "UP": return 0x26;
                case "RIGHT": return 0x27;
                case "DOWN": return 0x28;
                case "INSERT": return 0x2D;
                case "DELETE": return 0x2E;
                case "DEL": return 0x2E;
                case "X1": return 0x05;
                case "X2": return 0x06;
                case "XBUTTON1": return 0x05;
                case "XBUTTON2": return 0x06;
                case "MOUSE4": return 0x05;
                case "MOUSE5": return 0x06;
                case "MBUTTON": return 0x04;
                case "MOUSE3": return 0x04;
            }

            // 2. Check F-keys (F1 to F24)
            if (cleanKeyName.StartsWith("F") && cleanKeyName.Length > 1 && int.TryParse(cleanKeyName.Substring(1), out int fNum))
            {
                if (fNum >= 1 && fNum <= 24)
                {
                    return (byte)(0x6F + fNum); // VK_F1 is 0x70, VK_F24 is 0x87
                }
            }

            // 3. Check single letters or digits
            if (cleanKeyName.Length == 1)
            {
                char c = cleanKeyName[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    return (byte)c;
                }
            }

            return 0;
        }

        private static string VirtualKeyCodeToString(int vkey)
        {
            if (vkey >= 'A' && vkey <= 'Z') return ((char)vkey).ToString();
            if (vkey >= '0' && vkey <= '9') return ((char)vkey).ToString();
            if (vkey >= 0x70 && vkey <= 0x87) return "F" + (vkey - 0x6F);

            switch (vkey)
            {
                case 0x02: return "RBUTTON";
                case 0x04: return "MBUTTON";
                case 0x05: return "MOUSE4";
                case 0x06: return "MOUSE5";
                case 0x08: return "BACKSPACE";
                case 0x09: return "TAB";
                case 0x0D: return "ENTER";
                case 0x10: return "SHIFT";
                case 0x11: return "CTRL";
                case 0x12: return "ALT";
                case 0x14: return "CAPSLOCK";
                case 0x1B: return "ESC";
                case 0x20: return "SPACE";
                case 0x21: return "PAGEUP";
                case 0x22: return "PAGEDOWN";
                case 0x23: return "END";
                case 0x24: return "HOME";
                case 0x25: return "LEFT";
                case 0x26: return "UP";
                case 0x27: return "RIGHT";
                case 0x28: return "DOWN";
                case 0x2D: return "INSERT";
                case 0x2E: return "DELETE";
            }
            return "";
        }

        private void HandleKeyBinding()
        {
            if (_bindingIndex == -1) return;
            if ((DateTime.Now - _bindStartTime).TotalMilliseconds < 200) return; // Prevent instant trigger from mouse click

            for (int i = 1; i <= 254; i++)
            {
                if (i == 0x01) continue; // Ignore left click

                if ((GetAsyncKeyState(i) & 0x8000) != 0)
                {
                    if (i == 0x1B) // ESC cancels binding
                    {
                        _bindingIndex = -1;
                        SaveSettings();
                        break;
                    }

                    string keyName = VirtualKeyCodeToString(i);
                    if (string.IsNullOrEmpty(keyName)) continue; // Ignore unknown keys

                    if (_bindingIndex == 0)
                    {
                        ToggleKeyStr = keyName;
                        _lastHomePressed[keyName] = true;
                    }
                    else if (_bindingIndex == -2)
                    {
                        GlobalHideKey = keyName;
                    }
                    else
                    {
                        Skills[_bindingIndex - 1].Key = keyName;
                    }

                    _bindingIndex = -1;
                    SaveSettings();
                    break;
                }
            }
        }

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        private static void PressKey(byte vkey)
        {
            uint scanCode = MapVirtualKey(vkey, 0);
            
            // Press key down using Hardware Scan Code
            keybd_event(0, (byte)scanCode, KEYEVENTF_SCANCODE, UIntPtr.Zero);
            
            // Synchronous delay (longer hold for game engine compatibility)
            Thread.Sleep(50); 
            
            // Release key
            keybd_event(0, (byte)scanCode, KEYEVENTF_KEYUP | KEYEVENTF_SCANCODE, UIntPtr.Zero);
        }

        private Dictionary<string, bool> _lastHomePressed = new Dictionary<string, bool>();
        private bool _lastInsPressed = false;
        private Dictionary<string, bool> _lastSwitchPressed = new Dictionary<string, bool>();
        private DateTime _lastSwitchTime = DateTime.MinValue;

        private void AutoSwitchPresetOnFocus()
        {
            if (Presets.Count <= 1) return; // Don't auto-switch if only 1 preset
            
            // Pause auto-switching for 1 second after a manual hotkey switch.
            // This prevents the auto-switch from instantly reverting the preset because the OS hasn't brought the new window to foreground yet.
            if ((DateTime.Now - _lastSwitchTime).TotalMilliseconds < 1000) return;

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || hwnd == _overlayHandle) return;

            GetWindowThreadProcessId(hwnd, out uint activePid);
            
            // If current preset already matches the active window, do nothing (prevents flickering)
            string currentTarget = Presets[CurrentPresetIndex].TargetWindowName;
            if (!string.IsNullOrEmpty(currentTarget) && currentTarget.StartsWith("["))
            {
                int bIdx = currentTarget.IndexOf(']');
                if (bIdx > 1 && int.TryParse(currentTarget.Substring(1, bIdx - 1), out int currentPid))
                {
                    if (currentPid == activePid) return;
                }
            }
            
            for (int i = 0; i < Presets.Count; i++)
            {
                if (i == CurrentPresetIndex) continue;
                
                string targetStr = Presets[i].TargetWindowName;
                if (string.IsNullOrEmpty(targetStr) || targetStr == "[NONE - SELECT A GAME WINDOW]") continue;

                int bracketIndex = targetStr.IndexOf(']');
                if (bracketIndex > 1 && targetStr.StartsWith("["))
                {
                    if (int.TryParse(targetStr.Substring(1, bracketIndex - 1), out int targetPid))
                    {
                        if (activePid == targetPid)
                        {
                            CurrentPresetIndex = i; // Auto-switch UI to this preset
                            return;
                        }
                    }
                }
            }
        }

        private void HotkeyLoop()
        {
            while (true)
            {
                AutoSwitchPresetOnFocus();
                
                HashSet<string> processedKeys = new HashSet<string>();

                // Check Global Hotkeys for ALL presets using ToggleKeyStr
                for (int i = 0; i < Presets.Count; i++)
                {
                    var preset = Presets[i];
                    string hotkeyToUse = preset.ToggleKeyStr; // Use ToggleKeyStr as the unified hotkey
                    if (string.IsNullOrEmpty(hotkeyToUse)) hotkeyToUse = "HOME"; // Fallback

                    if (processedKeys.Contains(hotkeyToUse)) continue;

                    byte switchVk = GetVirtualKeyCode(hotkeyToUse);
                    if (switchVk != 0)
                    {
                        short switchState = GetAsyncKeyState(switchVk);
                        bool switchPressed = (switchState & 0x8000) != 0;
                        
                        _lastHomePressed.TryGetValue(hotkeyToUse, out bool lastPressed);
                        
                        if (switchPressed && !lastPressed)
                        {
                            // Determine if ANY preset using this hotkey is currently running
                            bool anyRunning = false;
                            for (int j = 0; j < Presets.Count; j++)
                            {
                                string h = Presets[j].ToggleKeyStr;
                                if (string.IsNullOrEmpty(h)) h = "HOME";
                                if (h == hotkeyToUse && Presets[j].IsMacroRunning)
                                {
                                    anyRunning = true;
                                    break;
                                }
                            }
                            
                            // Toggle the state for ALL presets sharing this hotkey
                            bool newState = !anyRunning;
                            for (int j = 0; j < Presets.Count; j++)
                            {
                                string h = Presets[j].ToggleKeyStr;
                                if (string.IsNullOrEmpty(h)) h = "HOME";
                                if (h == hotkeyToUse)
                                {
                                    Presets[j].IsMacroRunning = newState;
                                    
                                    // If we just turned it ON, bring the window to the front so it can start attacking!
                                    if (newState)
                                    {
                                        IntPtr hWnd = GetTargetWindowHandleForPreset(Presets[j]);
                                        if (hWnd != IntPtr.Zero) ForceForegroundWindow(hWnd);
                                    }
                                }
                            }
                            
                            CurrentPresetIndex = i;
                            _lastSwitchTime = DateTime.Now;
                        }
                        
                        _lastHomePressed[hotkeyToUse] = switchPressed;
                        processedKeys.Add(hotkeyToUse);
                    }
                }


                byte hideVk = GetVirtualKeyCode(GlobalHideKey);
                short insState = hideVk != 0 ? GetAsyncKeyState(hideVk) : (short)0;
                bool insPressed = (insState & 0x8000) != 0;

                if (insPressed && !_lastInsPressed)
                {
                    _showMenu = !_showMenu;
                    if (_overlayHandle != IntPtr.Zero)
                    {
                        ShowWindow(_overlayHandle, _showMenu ? SW_SHOW : SW_HIDE);
                    }
                }
                _lastInsPressed = insPressed;

                Thread.Sleep(5); // Fast polling for hotkeys
            }
        }

        private void MacroLoop()
        {
            while (true)
            {
                // Check ALL presets - send keys for whichever preset's window is currently active
                for (int i = 0; i < Presets.Count; i++)
                {
                    var preset = Presets[i];
                    if (!preset.IsMacroRunning) continue;

                    // Check if THIS preset's target window is the foreground window
                    if (!IsPresetWindowActive(preset)) continue;

                    var now = DateTime.Now;
                    foreach (var skill in preset.Skills)
                    {
                        if (!preset.IsMacroRunning) break;
                        if (skill.Enabled)
                        {
                            double elapsed = (now - skill.LastUsed).TotalSeconds;
                            if (elapsed >= skill.Cooldown)
                            {
                                byte vk = GetVirtualKeyCode(skill.Key);
                                if (vk != 0)
                                {
                                    PressKey(vk);
                                    skill.LastUsed = now;
                                }
                            }
                        }
                    }
                    break; // Only one window can be foreground at a time
                }
                Thread.Sleep(1);
            }
        }

        private bool _isCurrentlyTopMost = true;

        private bool _firstFrame = true;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        private const uint WM_SETICON = 0x0080;
        private const uint SWP_NOZORDER = 0x0004;

        protected override void Render()
        {
            if (_overlayHandle == IntPtr.Zero)
            {
                _overlayHandle = Process.GetCurrentProcess().MainWindowHandle;
            }

            if (_firstFrame && _overlayHandle != IntPtr.Zero)
            {
                _firstFrame = false;
                
                // Set window icon
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    IntPtr hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                    if (hIcon != IntPtr.Zero)
                    {
                        SendMessage(_overlayHandle, WM_SETICON, (IntPtr)0, hIcon); // ICON_SMALL
                        SendMessage(_overlayHandle, WM_SETICON, (IntPtr)1, hIcon); // ICON_BIG
                    }
                }
            }

            if (!_showMenu) return;

            if (_overlayHandle != IntPtr.Zero)
            {
                IntPtr fgHwnd = GetForegroundWindow();
                bool shouldBeTopmost = (fgHwnd == _overlayHandle);
                
                if (shouldBeTopmost != _isCurrentlyTopMost)
                {
                    _isCurrentlyTopMost = shouldBeTopmost;
                    SetWindowPos(_overlayHandle, shouldBeTopmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }

            if (!_sizeSet)
            {
                _sizeSet = true;
                Size = new System.Drawing.Size(GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
                Position = new System.Drawing.Point(0, 0);
            }

            InitializeStyle();

            if (!_showMenu) return;

            HandleKeyBinding();

            var io = ImGui.GetIO();
            io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableKeyboard;
            io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableGamepad;
            
            ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.5f), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));

            bool isOpen = true;
            ImGui.Begin("Auto Skill", ref isOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize);
            if (!isOpen)
            {
                Close();
            }

            if (ImGui.CollapsingHeader("Preset Profiles", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.BeginTable("ProfileTable", 2, ImGuiTableFlags.None))
                {
                    ImGui.TableSetupColumn("Labels", ImGuiTableColumnFlags.WidthFixed, 110f);
                    ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthStretch);
                    
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Profile:");
                    
                    ImGui.TableNextColumn();
                    float newBtnWidth = 45f;
                    float delBtnWidth = 60f;
                    float spacing = ImGui.GetStyle().ItemSpacing.X;
                    float comboWidth = ImGui.GetContentRegionAvail().X - newBtnWidth - delBtnWidth - (spacing * 2);

                    ImGui.SetNextItemWidth(comboWidth);
                    string[] presetNames = Presets.Select(p => p.Name).ToArray();
                    if (ImGui.Combo("##Preset", ref CurrentPresetIndex, presetNames, presetNames.Length))
                    {
                        MacroRunning = false;
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("New", new Vector2(newBtnWidth, 0))) _showNewPresetPopup = true;
                    
                    ImGui.SameLine();
                    if (ImGui.Button("Delete", new Vector2(delBtnWidth, 0)) && Presets.Count > 1)
                    {
                        Presets.RemoveAt(CurrentPresetIndex);
                        if (CurrentPresetIndex >= Presets.Count) CurrentPresetIndex = Presets.Count - 1;
                        SaveSettings();
                        MacroRunning = false;
                    }
                    
                    ImGui.EndTable();
                }

                if (_showNewPresetPopup)
                {
                    ImGui.InputText("##NewPresetName", ref _newPresetName, 32);
                    ImGui.SameLine();
                    if (ImGui.Button("Add"))
                    {
                        if (!string.IsNullOrWhiteSpace(_newPresetName))
                        {
                            var newPreset = new PresetConfig 
                            { 
                                Name = _newPresetName, 
                                Skills = CreateDefaultSkills(),
                                ToggleKeyStr = "HOME"
                            };
                            Presets.Add(newPreset);
                            CurrentPresetIndex = Presets.Count - 1;
                            _newPresetName = "";
                            _showNewPresetPopup = false;
                            SaveSettings();
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                    {
                        _newPresetName = "";
                        _showNewPresetPopup = false;
                    }
                }
            }

            if (_showNewPresetPopup) ImGui.BeginDisabled();

            if (ImGui.CollapsingHeader("Menu", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.BeginTable("MenuTable", 2, ImGuiTableFlags.None))
                {
                    ImGui.TableSetupColumn("Labels", ImGuiTableColumnFlags.WidthFixed, 110f);
                    ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthStretch);
                    
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool macroState = MacroRunning;
                    if (ImGui.Checkbox("Enable Macro", ref macroState))
                    {
                        MacroRunning = macroState;
                        if (MacroRunning) ActivateTargetWindow();
                    }
                    
                    ImGui.TableNextColumn();
                    string toggleBtn = (_bindingIndex == 0) ? "Press any key..." : ToggleKeyStr;
                    if (ImGui.Button(toggleBtn, new Vector2(-1, 0)))
                    {
                        if (_bindingIndex == -1) {
                            _bindingIndex = 0;
                            _bindStartTime = DateTime.Now;
                        }
                    }
                    
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Hide Menu");
                    
                    ImGui.TableNextColumn();
                    string hideBtn = (_bindingIndex == -2) ? "Press any key..." : GlobalHideKey;
                    if (ImGui.Button(hideBtn, new Vector2(-1, 0)))
                    {
                        if (_bindingIndex == -1) {
                            _bindingIndex = -2;
                            _bindStartTime = DateTime.Now;
                        }
                    }
                    
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Game Window");
                    
                    ImGui.TableNextColumn();
                    RefreshWindowList();
                    ImGui.SetNextItemWidth(-1);
                    string[] windowsArray = _availableWindows.ToArray();
                    
                    string currentTarget = Presets.Count > 0 ? Presets[CurrentPresetIndex].TargetWindowName : "";
                    int idx = _availableWindows.IndexOf(currentTarget);
                    
                    // If exact match fails (e.g. PID changed), try matching by title
                    if (idx == -1 && !string.IsNullOrEmpty(currentTarget) && currentTarget != "[NONE - SELECT A GAME WINDOW]")
                    {
                        string savedTitle = ExtractTitleFromTarget(currentTarget);
                        for (int i = 0; i < _availableWindows.Count; i++)
                        {
                            if (ExtractTitleFromTarget(_availableWindows[i]) == savedTitle)
                            {
                                idx = i;
                                // Auto-update to new PID string
                                Presets[CurrentPresetIndex].TargetWindowName = _availableWindows[i];
                                SaveSettings();
                                break;
                            }
                        }
                    }
                    
                    if (idx == -1 && _availableWindows.Count > 0) idx = 0; // Fallback

                    if (ImGui.Combo("##GameWindow", ref idx, windowsArray, windowsArray.Length))
                    {
                        if (Presets.Count > 0 && idx >= 0 && idx < _availableWindows.Count)
                        {
                            Presets[CurrentPresetIndex].TargetWindowName = _availableWindows[idx];
                            SaveSettings();
                        }
                    }
                    
                    ImGui.EndTable();
                }

                ImGui.Dummy(new Vector2(0, 5));
                bool targetActive = IsTargetWindowActive();
                if (MacroRunning)
                {
                    if (targetActive) ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1.0f), "Status: Active & Hooked");
                    else ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), "Status: Waiting for Window...");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Status: Stopped");
                }
            }

            if (ImGui.CollapsingHeader("Primary Skills (1-0)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                RenderSkillsTable("Table1", 0, 10);
            }

            if (ImGui.CollapsingHeader("Function Skills (F1-F10)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                RenderSkillsTable("Table2", 10, 20);
            }

            ImGui.Dummy(new Vector2(0, 5));
            string creditText = "Version 1.1";
            float textWidth = ImGui.CalcTextSize(creditText).X;
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - textWidth - 10);
            ImGui.TextDisabled(creditText);

            if (_showNewPresetPopup) ImGui.EndDisabled();

            ImGui.End();
        }

        private void RenderSkillsTable(string id, int startIdx, int endIdx)
        {
            ImGui.Dummy(new Vector2(0, 5));
            if (ImGui.BeginTable(id, 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("Hotkey", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Cooldown", ImGuiTableColumnFlags.WidthFixed, 210f);
                ImGui.TableHeadersRow();

                for (int i = startIdx; i < endIdx; i++)
                {
                    var skill = Skills[i];
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (45f - 24f) / 2f); // Perfect centering
                    bool changed = false;

                    if (ImGui.Checkbox($"##chk{i}", ref skill.Enabled)) changed = true;

                    ImGui.TableSetColumnIndex(1);
                    ImGui.SetNextItemWidth(-1);
                    string skillBtn = (_bindingIndex == i + 1) ? "..." : skill.Key;
                    if (string.IsNullOrEmpty(skillBtn)) skillBtn = "NONE";
                    
                    if (ImGui.Button($"{skillBtn}##btn{i}", new Vector2(-1, 0)))
                    {
                        if (_bindingIndex == -1) {
                            _bindingIndex = i + 1;
                            _bindStartTime = DateTime.Now;
                        }
                    }

                    ImGui.TableSetColumnIndex(2);
                    ImGui.SetNextItemWidth(-1);
                    ImGui.DragFloat($"##cd{i}", ref skill.Cooldown, 0.0f, 0.00f, 10.0f, "%.2fs");
                    if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;
                    
                    if (skill.Cooldown < 0.00f) skill.Cooldown = 0.00f;

                    if (changed) SaveSettings();
                }
                ImGui.EndTable();
            }
        }
    }
}
