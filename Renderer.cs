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

    public class Renderer : Overlay
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        public volatile bool MacroRunning = false;
        public List<Skill> Skills = new List<Skill>();
        public string ToggleKeyStr = "HOME";

        private bool _styleInitialized = false;
        private bool _sizeSet = false;
        private bool _lastHomePressed = false;
        private volatile bool _showMenu = true;
        private bool _lastInsPressed = false;
        private IntPtr _overlayHandle = IntPtr.Zero;

        private int _bindingIndex = -1; // -1: none, 0: toggle key, 1+: skills
        private DateTime _bindStartTime;

        private List<string> _availableWindows = new List<string>() { "[NONE - SELECT A GAME WINDOW]" };
        private int _selectedWindowIndex = 0;
        private DateTime _lastWindowRefresh = DateTime.MinValue;

        // Win32 API Imports for global keyboard control
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const int KEYEVENTF_SCANCODE = 0x0008;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private void ActivateTargetWindow()
        {
            if (_selectedWindowIndex > 0 && _selectedWindowIndex < _availableWindows.Count)
            {
                string targetTitle = _availableWindows[_selectedWindowIndex];
                var process = Process.GetProcesses().FirstOrDefault(p => p.MainWindowTitle == targetTitle);
                if (process != null && process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(process.MainWindowHandle);
                }
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

        private bool IsTargetWindowActive()
        {
            if (_selectedWindowIndex > 0 && _selectedWindowIndex < _availableWindows.Count)
            {
                string targetTitle = _availableWindows[_selectedWindowIndex];
                string currentTitle = GetActiveWindowTitle();
                if (!string.IsNullOrEmpty(currentTitle) && currentTitle == targetTitle)
                {
                    return true;
                }
            }
            return false;
        }

        private void RefreshWindowList()
        {
            if (MacroRunning) return; // Do not refresh and risk resetting while macro is active

            if ((DateTime.Now - _lastWindowRefresh).TotalSeconds < 2) return;
            _lastWindowRefresh = DateTime.Now;

            var currentList = Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                .Select(p => p.MainWindowTitle)
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            currentList.Insert(0, "[NONE - SELECT A GAME WINDOW]");

            string currentSelected = _selectedWindowIndex >= 0 && _selectedWindowIndex < _availableWindows.Count ? _availableWindows[_selectedWindowIndex] : "";
            
            _availableWindows = currentList;
            _selectedWindowIndex = _availableWindows.IndexOf(currentSelected);
            
            if (_selectedWindowIndex == -1 && !string.IsNullOrEmpty(currentSelected) && currentSelected != "[NONE - SELECT A GAME WINDOW]")
            {
                // Fallback to partial match if game title changed dynamically (e.g. FPS counter)
                _selectedWindowIndex = _availableWindows.FindIndex(w => w.Contains(currentSelected) || currentSelected.Contains(w));
            }

            if (_selectedWindowIndex == -1) _selectedWindowIndex = 0;
        }

        private void SaveSettings()
        {
            try
            {
                var options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
                string json = JsonSerializer.Serialize(Skills, options);
                File.WriteAllText("config.json", json);
                File.WriteAllText("hotkey.txt", ToggleKeyStr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSkill] SaveSettings failed: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists("config.json"))
                {
                    string json = File.ReadAllText("config.json");
                    var options = new JsonSerializerOptions { IncludeFields = true };
                    var loaded = JsonSerializer.Deserialize<List<Skill>>(json, options);
                    if (loaded != null)
                    {
                        int count = Math.Min(loaded.Count, Skills.Count);
                        for (int i = 0; i < count; i++)
                        {
                            Skills[i].Enabled = loaded[i].Enabled;
                            Skills[i].Key = loaded[i].Key;
                            Skills[i].Cooldown = loaded[i].Cooldown;
                        }
                    }
                }
                if (File.Exists("hotkey.txt"))
                {
                    ToggleKeyStr = File.ReadAllText("hotkey.txt").Trim();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSkill] LoadSettings failed: {ex.Message}");
            }
        }

        public Renderer()
        {
            // Initialize 20 hotkeys (1-0 and F1-F10)
            string[] keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10" };
            foreach (var k in keys)
            {
                Skills.Add(new Skill { Key = k });
            }

            LoadSettings();

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
                        break;
                    }

                    string keyName = VirtualKeyCodeToString(i);
                    if (string.IsNullOrEmpty(keyName)) continue; // Ignore unknown keys

                    if (_bindingIndex == 0)
                    {
                        ToggleKeyStr = keyName;
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

        private static void PressKey(byte vkey)
        {
            uint scanCode = MapVirtualKey(vkey, 0);
            
            // Press key down using Hardware Scan Code
            keybd_event(0, (byte)scanCode, KEYEVENTF_SCANCODE, UIntPtr.Zero);
            
            // Synchronous delay (prevents OS input buffer overflow which causes stuck keys!)
            Thread.Sleep(15); 
            
            // Release key
            keybd_event(0, (byte)scanCode, KEYEVENTF_KEYUP | KEYEVENTF_SCANCODE, UIntPtr.Zero);
        }

        private void HotkeyLoop()
        {
            while (true)
            {
                byte toggleVk = GetVirtualKeyCode(ToggleKeyStr);
                if (toggleVk == 0) toggleVk = 0x24; // Fallback to HOME

                short homeState = GetAsyncKeyState(toggleVk);
                bool homePressed = (homeState & 0x8000) != 0;

                if (homePressed && !_lastHomePressed)
                {
                    MacroRunning = !MacroRunning;
                    if (MacroRunning) ActivateTargetWindow();
                }
                _lastHomePressed = homePressed;

                short insState = GetAsyncKeyState(0x2D);
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
                if (MacroRunning && IsTargetWindowActive())
                {
                    var now = DateTime.Now;
                    foreach (var skill in Skills)
                    {
                        if (!MacroRunning) break; // Exit immediately if toggled off
                        if (skill.Enabled)
                        {
                            double elapsed = (now - skill.LastUsed).TotalSeconds;
                            if (elapsed >= skill.Cooldown)
                            {
                                byte vk = GetVirtualKeyCode(skill.Key);
                                if (vk != 0)
                                {
                                    PressKey(vk);
                                    skill.LastUsed = now; // ULTRA FAST
                                }
                            }
                        }
                    }
                }
                Thread.Sleep(1); // 1ms sleep for ultra-fast polling
            }
        }

        protected override void Render()
        {
            if (_overlayHandle == IntPtr.Zero)
            {
                _overlayHandle = Process.GetCurrentProcess().MainWindowHandle;
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

            // Center window on first launch. It will remember its position automatically afterwards.
            var io = ImGui.GetIO();
            ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.5f), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));

            bool isOpen = true;
            ImGui.Begin("Auto Skill", ref isOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize);
            if (!isOpen)
            {
                Close();
            }

            if (ImGui.CollapsingHeader("Menu", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool macroState = MacroRunning;
                if (ImGui.Checkbox($"Enable Macro [{ToggleKeyStr}]", ref macroState))
                {
                    MacroRunning = macroState;
                    if (MacroRunning) ActivateTargetWindow();
                }
                ImGui.SameLine();
                string toggleBtn = (_bindingIndex == 0) ? "Press any key..." : $"Change Key: {ToggleKeyStr}";
                if (ImGui.Button(toggleBtn, new Vector2(150, 0)))
                {
                    if (_bindingIndex == -1) {
                        _bindingIndex = 0;
                        _bindStartTime = DateTime.Now;
                    }
                }

                ImGui.Text("Target Game Window:");
                RefreshWindowList();
                ImGui.SetNextItemWidth(300);
                string[] windowsArray = _availableWindows.ToArray();
                ImGui.Combo("##GameWindow", ref _selectedWindowIndex, windowsArray, windowsArray.Length);

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
            string creditText = "Version 1.0";
            float textWidth = ImGui.CalcTextSize(creditText).X;
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - textWidth - 10);
            ImGui.TextDisabled(creditText);

            ImGui.End();
        }

        private void RenderSkillsTable(string id, int startIdx, int endIdx)
        {
            ImGui.Dummy(new Vector2(0, 5));
            if (ImGui.BeginTable(id, 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed, 55f);
                ImGui.TableSetupColumn("Hotkey", ImGuiTableColumnFlags.WidthFixed, 85f);
                ImGui.TableSetupColumn("Cooldown", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                for (int i = startIdx; i < endIdx; i++)
                {
                    var skill = Skills[i];
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (55f - 24f) / 2f); // Perfect centering
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
                    ImGui.DragFloat($"##cd{i}", ref skill.Cooldown, 0.01f, 0.00f, 10.0f, "%.2fs");
                    if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;
                    
                    if (skill.Cooldown < 0.00f) skill.Cooldown = 0.00f;

                    if (changed) SaveSettings();
                }
                ImGui.EndTable();
            }
        }
    }
}