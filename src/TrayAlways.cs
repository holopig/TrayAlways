// TrayAlways — keeps every Windows 11 notification-area (tray) icon visible on the taskbar.
//
// How it works
//   Windows 11 stores one subkey per tray icon under HKCU\Control Panel\NotifyIconSettings.
//   The DWORD "IsPromoted" decides where the icon lives: 1 = on the taskbar, 0/absent = in the
//   overflow ("^") flyout. New icons arrive without IsPromoted, so they are always hidden.
//   TrayAlways watches that key (RegNotifyChangeKeyValue, no polling), sets IsPromoted=1 on
//   every entry, then writes+deletes a temporary value in the entry so Explorer's per-icon
//   registry watcher re-reads it and moves the icon right away (no Explorer restart).
//
//   If you switch an icon OFF in Settings > Personalization > Taskbar > Other system tray icons
//   while TrayAlways runs, that choice is remembered as an exception; switching it back ON
//   removes the exception. So the Settings page keeps working as the per-icon UI.
//
// Build: see build.cmd (uses the C# compiler that ships with .NET Framework 4.x; no SDK needed).
// C# 5 only (csc.exe 4.x): no string interpolation, no ?. operator, no expression-bodied members.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("TrayAlways")]
[assembly: AssemblyDescription("Keeps every Windows 11 tray icon visible on the taskbar")]
[assembly: AssemblyProduct("TrayAlways")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

namespace TrayAlways
{
    static class Native
    {
        public const uint REG_NOTIFY_CHANGE_NAME = 0x1;
        public const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;

        [DllImport("advapi32.dll")]
        public static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool watchSubtree, uint notifyFilter, SafeWaitHandle hEvent, bool asynchronous);

        [DllImport("shell32.dll")]
        public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int processId);

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    // UI strings. Each language is an embedded resource "lang.<culture>" built from lang\<culture>.txt
    // ("key = value" lines, UTF-8). Missing keys fall back to English.
    static class L
    {
        sealed class Lang { public string Code, NativeName, EnglishName; public Dictionary<string, string> Strings; }

        static readonly Dictionary<string, Lang> All = new Dictionary<string, Lang>(StringComparer.OrdinalIgnoreCase);
        static Lang current, english;
        public static string Code { get { return current == null ? "en" : current.Code; } }
        public static bool Rtl { get; private set; }

        static L()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith("lang.", StringComparison.Ordinal)) continue;
                Lang l = new Lang();
                l.Code = res.Substring(5);
                l.Strings = new Dictionary<string, string>(StringComparer.Ordinal);
                using (StreamReader r = new StreamReader(asm.GetManifestResourceStream(res), Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        l.Strings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                    }
                }
                string n;
                l.NativeName = l.Strings.TryGetValue("_name", out n) ? n : l.Code;
                try { l.EnglishName = new CultureInfo(l.Code).EnglishName; } catch { l.EnglishName = l.Code; }
                All[l.Code] = l;
            }
            Lang en;
            All.TryGetValue("en", out en);
            english = en;
            Use(null);
        }

        // Windows display language -> best available translation.
        public static string Detect()
        {
            CultureInfo c = CultureInfo.CurrentUICulture;
            for (CultureInfo x = c; !string.IsNullOrEmpty(x.Name); x = x.Parent)
                if (All.ContainsKey(x.Name)) return x.Name;
            string two = c.TwoLetterISOLanguageName;
            if (two == "no" || two == "nn") two = "nb";
            if (two == "pt" && c.Name.Length > 2 && All.ContainsKey("pt-PT")) return "pt-PT"; // pt-AO, pt-MZ… follow European Portuguese
            if (All.ContainsKey(two)) return two;
            foreach (string code in All.Keys.OrderBy(k => k, StringComparer.Ordinal))
                if (code.StartsWith(two + "-", StringComparison.OrdinalIgnoreCase)) return code; // pt -> pt-BR, sr -> sr-Latn
            return "en";
        }

        // code == null or "" means "follow Windows".
        public static void Use(string code)
        {
            Lang l;
            if (string.IsNullOrEmpty(code) || !All.TryGetValue(code, out l)) All.TryGetValue(Detect(), out l);
            current = l ?? english;
            try { Rtl = current != null && new CultureInfo(current.Code).TextInfo.IsRightToLeft; } catch { Rtl = false; }
        }

        public static string T(string key)
        {
            string s;
            if (current != null && current.Strings.TryGetValue(key, out s)) return s;
            if (english != null && english.Strings.TryGetValue(key, out s)) return s;
            return key;
        }

        public static string F(string key, params object[] args)
        {
            if (Rtl)
            {
                // Keep left-to-right fragments (shortcuts, app names) intact inside Arabic/Hebrew text.
                object[] wrapped = new object[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    string s = Convert.ToString(args[i], CultureInfo.InvariantCulture);
                    wrapped[i] = Regex.IsMatch(s, "[A-Za-z]") ? "‪" + s + "‬" : args[i];
                }
                args = wrapped;
            }
            return string.Format(T(key), args);
        }

        public static string NativeNameOf(string code) { Lang l; return All.TryGetValue(code, out l) ? l.NativeName : code; }

        // Lists every language with missing keys and placeholder mismatches against English.
        public static int Check(TextWriter w)
        {
            int problems = 0;
            Regex ph = new Regex(@"\{\d\}");
            w.WriteLine("Windows UI culture: {0} -> detected translation: {1}", CultureInfo.CurrentUICulture.Name, Detect());
            foreach (Lang l in All.Values.OrderBy(x => x.Code))
            {
                List<string> issues = new List<string>();
                foreach (KeyValuePair<string, string> e in english.Strings)
                {
                    string v;
                    if (!l.Strings.TryGetValue(e.Key, out v)) { issues.Add("missing " + e.Key); continue; }
                    string a = string.Join("", ph.Matches(e.Value).Cast<Match>().Select(m => m.Value).OrderBy(s => s));
                    string b = string.Join("", ph.Matches(v).Cast<Match>().Select(m => m.Value).OrderBy(s => s));
                    if (a != b) issues.Add("placeholders " + e.Key + " (" + b + " vs " + a + ")");
                }
                foreach (string k in l.Strings.Keys) if (!english.Strings.ContainsKey(k)) issues.Add("unknown " + k);
                problems += issues.Count;
                w.WriteLine("{0,-8} {1,-26} {2,3} keys  {3}", l.Code, l.NativeName, l.Strings.Count, issues.Count == 0 ? "OK" : string.Join("; ", issues));
            }
            w.WriteLine("{0} languages, {1} problems", All.Count, problems);
            return problems;
        }

        // (code, native name) sorted by English name so the list is stable for everyone.
        public static List<KeyValuePair<string, string>> Available()
        {
            return All.Values.OrderBy(l => l.EnglishName, StringComparer.OrdinalIgnoreCase)
                .Select(l => new KeyValuePair<string, string>(l.Code, l.NativeName)).ToList();
        }
    }

    // Global shortcut that brings back the hidden TrayAlways icon.
    sealed class Hotkey
    {
        public uint Modifiers; // MOD_ALT | MOD_CONTROL | MOD_SHIFT
        public Keys Key;

        public static readonly Hotkey Default = new Hotkey { Modifiers = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, Key = Keys.T };

        public bool IsValid { get { return Key != Keys.None && (Modifiers & (Native.MOD_CONTROL | Native.MOD_ALT)) != 0; } }

        public string ModifiersText()
        {
            StringBuilder sb = new StringBuilder();
            if ((Modifiers & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((Modifiers & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((Modifiers & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            return sb.ToString();
        }

        public override string ToString() { return ModifiersText() + KeyName(Key); }

        public static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (int)(k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.Oem6: return "]";
                case Keys.Oem5: return "\\";
                case Keys.Oem1: return ";";
                case Keys.Oem7: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Next: return "PageDown";
                case Keys.Prior: return "PageUp";
                default: return k.ToString();
            }
        }

        public static Hotkey FromKeyEvent(KeyEventArgs e)
        {
            Hotkey h = new Hotkey();
            if (e.Control) h.Modifiers |= Native.MOD_CONTROL;
            if (e.Alt) h.Modifiers |= Native.MOD_ALT;
            if (e.Shift) h.Modifiers |= Native.MOD_SHIFT;
            Keys k = e.KeyCode;
            bool modifierOnly = k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin
                             || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.LMenu || k == Keys.RMenu;
            h.Key = modifierOnly ? Keys.None : k;
            return h;
        }
    }

    // Small "press a new shortcut" dialog.
    sealed class HotkeyDialog : Form
    {
        readonly TextBox box;
        readonly Func<Hotkey, bool> tryApply;
        Hotkey candidate;

        public HotkeyDialog(Hotkey current, Func<Hotkey, bool> tryApply)
        {
            this.tryApply = tryApply;
            Text = "TrayAlways";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            AutoScaleMode = AutoScaleMode.Font;
            RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
            RightToLeftLayout = L.Rtl;
            Icon = AppIcon.ForTray();

            TableLayoutPanel t = new TableLayoutPanel();
            t.AutoSize = true; t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Padding = new Padding(12); t.ColumnCount = 1;
            Label prompt = new Label();
            prompt.Text = L.T("hotkey_prompt"); prompt.AutoSize = true; prompt.MaximumSize = new Size(420, 0);
            prompt.Margin = new Padding(0, 0, 0, 8);
            box = new TextBox();
            box.ReadOnly = true; box.TabStop = false; box.Width = 300; box.TextAlign = HorizontalAlignment.Center;
            box.Font = new Font(Font.FontFamily, Font.Size * 1.3f, FontStyle.Bold);
            box.Text = current.ToString();
            candidate = current;
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft; buttons.AutoSize = true; buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 12, 0, 0);
            Button cancel = new Button(); cancel.Text = L.T("cancel"); cancel.DialogResult = DialogResult.Cancel; cancel.AutoSize = true;
            Button ok = new Button(); ok.Text = L.T("ok"); ok.AutoSize = true;
            ok.Click += delegate
            {
                if (!candidate.IsValid) return;
                if (tryApply(candidate)) { DialogResult = DialogResult.OK; Close(); }
                else MessageBox.Show(this, L.T("hotkey_taken"), "TrayAlways", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            t.Controls.Add(prompt); t.Controls.Add(box); t.Controls.Add(buttons);
            Controls.Add(t);
            AcceptButton = ok; CancelButton = cancel;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control || e.Alt)
            {
                Hotkey h = Hotkey.FromKeyEvent(e);
                box.Text = h.Key == Keys.None ? h.ModifiersText() + "…" : h.ToString();
                if (h.IsValid) candidate = h;
                e.Handled = true;
                e.SuppressKeyPress = true; // keep Alt from opening the window menu
                return;
            }
            base.OnKeyDown(e);
        }
    }

    static class Log
    {
        static readonly object Sync = new object();
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayAlways");
        public static readonly string FilePath = Path.Combine(Dir, "TrayAlways.log");
        public static TextWriter Echo; // console in CLI modes

        public static void Write(string format, params object[] args)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + (args.Length > 0 ? string.Format(format, args) : format);
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    FileInfo fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
                if (Echo != null) { try { Echo.WriteLine(line); } catch { } }
            }
        }
    }

    // Persistent settings in HKCU\Software\TrayAlways
    sealed class Config
    {
        const string KeyPath = @"Software\TrayAlways";
        public bool Enabled = true;
        public bool RespectManualHide = true;
        public bool FirstRunDone;
        public string Language = "";      // "" = follow Windows display language
        public bool IconHidden;
        public bool HideHintShown;
        public Hotkey ShowHotkey = Hotkey.Default;
        public HashSet<string> Exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k != null)
                    {
                        c.Enabled = Convert.ToInt32(k.GetValue("Enabled", 1)) != 0;
                        c.RespectManualHide = Convert.ToInt32(k.GetValue("RespectManualHide", 1)) != 0;
                        c.FirstRunDone = Convert.ToInt32(k.GetValue("FirstRunDone", 0)) != 0;
                        c.Language = k.GetValue("Language") as string ?? "";
                        c.IconHidden = Convert.ToInt32(k.GetValue("IconHidden", 0)) != 0;
                        c.HideHintShown = Convert.ToInt32(k.GetValue("HideHintShown", 0)) != 0;
                        Hotkey h = new Hotkey();
                        h.Modifiers = (uint)Convert.ToInt32(k.GetValue("HotkeyModifiers", (int)Hotkey.Default.Modifiers));
                        h.Key = (Keys)Convert.ToInt32(k.GetValue("HotkeyKey", (int)Hotkey.Default.Key));
                        if (h.IsValid) c.ShowHotkey = h;
                        string[] ex = k.GetValue("Exclusions") as string[];
                        if (ex != null) foreach (string s in ex) if (!string.IsNullOrEmpty(s)) c.Exclusions.Add(s);
                    }
                }
            }
            catch (Exception e) { Log.Write("Config load failed: {0}", e.Message); }
            return c;
        }

        public void Save()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("Enabled", Enabled ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("RespectManualHide", RespectManualHide ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("FirstRunDone", FirstRunDone ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("Language", Language ?? "", RegistryValueKind.String);
                    k.SetValue("IconHidden", IconHidden ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("HideHintShown", HideHintShown ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("HotkeyModifiers", (int)ShowHotkey.Modifiers, RegistryValueKind.DWord);
                    k.SetValue("HotkeyKey", (int)ShowHotkey.Key, RegistryValueKind.DWord);
                    k.SetValue("Exclusions", Exclusions.OrderBy(s => s).ToArray(), RegistryValueKind.MultiString);
                }
            }
            catch (Exception e) { Log.Write("Config save failed: {0}", e.Message); }
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "TrayAlways";

        public static bool IsEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                string v = k == null ? null : k.GetValue(Name) as string;
                return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public static void Set(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else if (k.GetValue(Name) != null) k.DeleteValue(Name);
            }
            Log.Write("Autostart {0}", on ? "enabled" : "disabled");
        }
    }

    sealed class IconEntry
    {
        public string Id;          // subkey name (hash chosen by Explorer)
        public string ExePath;     // as stored, may start with a known-folder GUID
        public string Tooltip;
        public int Promoted;       // 1, 0, or -1 when the value is absent
        public string Identity;    // stable across app updates (version numbers stripped)
        public string DisplayName;
    }

    static class TrayRegistry
    {
        public const string KeyPath = @"Control Panel\NotifyIconSettings";
        public const string PromotedValue = "IsPromoted";
        const string TouchValue = "_temp_TrayAlways_refresh";
        static readonly Regex VersionLike = new Regex(@"\d+(?:[._]\d+)+", RegexOptions.Compiled);
        static readonly Dictionary<string, string> NameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static List<IconEntry> ReadAll()
        {
            List<IconEntry> list = new List<IconEntry>();
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (root == null) return list;
                foreach (string id in root.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey k = root.OpenSubKey(id))
                        {
                            if (k == null) continue;
                            IconEntry e = new IconEntry();
                            e.Id = id;
                            e.ExePath = k.GetValue("ExecutablePath") as string ?? "";
                            e.Tooltip = (k.GetValue("InitialTooltip") as string ?? "").Trim();
                            object p = k.GetValue(PromotedValue);
                            e.Promoted = p is int ? (int)p : -1;
                            object guid = k.GetValue("IconGuid");
                            object uid = k.GetValue("UID");
                            string sub = guid != null ? "guid:" + guid : (uid != null ? "uid:" + uid : "id:" + id);
                            e.Identity = VersionLike.Replace(e.ExePath, "#").ToLowerInvariant() + "|" + sub;
                            e.DisplayName = DisplayNameFor(e);
                            list.Add(e);
                        }
                    }
                    catch (Exception ex) { Log.Write("Read {0} failed: {1}", id, ex.Message); }
                }
            }
            return list;
        }

        // Sets IsPromoted on one entry and pokes Explorer's per-icon watcher.
        public static bool SetPromoted(string id, int value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath + "\\" + id, true))
                {
                    if (k == null) return false;
                    k.SetValue(PromotedValue, value, RegistryValueKind.DWord);
                    k.SetValue(TouchValue, "", RegistryValueKind.String);
                    k.DeleteValue(TouchValue, false);
                    return true;
                }
            }
            catch (Exception ex) { Log.Write("Write {0} failed: {1}", id, ex.Message); return false; }
        }

        public static string ResolvePath(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (stored.StartsWith("{"))
            {
                int end = stored.IndexOf('}');
                Guid g;
                if (end > 0 && Guid.TryParse(stored.Substring(0, end + 1), out g))
                {
                    IntPtr p;
                    if (Native.SHGetKnownFolderPath(g, 0, IntPtr.Zero, out p) == 0)
                    {
                        string folder = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                        return folder + stored.Substring(end + 1);
                    }
                }
            }
            return stored;
        }

        static string DisplayNameFor(IconEntry e)
        {
            string file = Path.GetFileName(e.ExePath);
            if (string.IsNullOrEmpty(file)) return e.Id;
            string key = e.ExePath;
            string desc;
            lock (NameCache)
            {
                if (!NameCache.TryGetValue(key, out desc))
                {
                    desc = null;
                    try
                    {
                        string full = ResolvePath(e.ExePath);
                        if (File.Exists(full))
                        {
                            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(full);
                            desc = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim()
                                 : (!string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim() : null);
                        }
                    }
                    catch { }
                    NameCache[key] = desc;
                }
            }
            string name = desc ?? Path.GetFileNameWithoutExtension(file);
            // explorer.exe hosts several system icons; the tooltip tells them apart
            if (file.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && e.Tooltip.Length > 0) name = e.Tooltip;
            return name;
        }
    }

    // The core: one pass over all entries, plus the "user hid it in Settings" heuristic.
    sealed class Promoter
    {
        readonly Config config;
        readonly object sync = new object();
        readonly Dictionary<string, int> lastSeen = new Dictionary<string, int>();
        readonly Dictionary<string, DateTime> promotedAt = new Dictionary<string, DateTime>();
        readonly Dictionary<string, int> fightCount = new Dictionary<string, int>();
        static readonly TimeSpan ExplorerGrace = TimeSpan.FromSeconds(15);

        public event EventHandler ExclusionsChanged;

        public Promoter(Config config) { this.config = config; }

        public int Pass(string reason)
        {
            lock (sync)
            {
                int changed = 0;
                bool exclusionsChanged = false;
                List<IconEntry> entries = TrayRegistry.ReadAll();
                foreach (IconEntry e in entries)
                {
                    int prev;
                    bool known = lastSeen.TryGetValue(e.Id, out prev);
                    bool excluded = config.Exclusions.Contains(e.Identity);

                    if (excluded)
                    {
                        // Turned back ON in Settings -> drop the exception.
                        if (e.Promoted == 1 && known && prev != 1)
                        {
                            config.Exclusions.Remove(e.Identity);
                            exclusionsChanged = true;
                            Log.Write("Re-enabled by user in Settings: {0}", e.DisplayName);
                        }
                        lastSeen[e.Id] = e.Promoted;
                        continue;
                    }

                    if (e.Promoted == 1) { lastSeen[e.Id] = 1; continue; }

                    if (!config.Enabled) { lastSeen[e.Id] = e.Promoted; continue; }

                    // 1 -> 0 while we are running: either the user switched it off in Settings,
                    // or Explorer demoted an icon we just promoted. Explorer gets a grace window.
                    if (config.RespectManualHide && known && prev == 1 && e.Promoted == 0)
                    {
                        DateTime at;
                        bool recentlyOurs = promotedAt.TryGetValue(e.Id, out at) && DateTime.UtcNow - at < ExplorerGrace;
                        if (!recentlyOurs)
                        {
                            config.Exclusions.Add(e.Identity);
                            exclusionsChanged = true;
                            lastSeen[e.Id] = 0;
                            Log.Write("Hidden by user in Settings, keeping it hidden: {0} [{1}]", e.DisplayName, e.Identity);
                            continue;
                        }
                        int fights;
                        fightCount.TryGetValue(e.Id, out fights);
                        fightCount[e.Id] = ++fights;
                        if (fights > 5)
                        {
                            lastSeen[e.Id] = 0;
                            if (fights == 6) Log.Write("Explorer keeps demoting {0}; giving up on it for this session", e.DisplayName);
                            continue;
                        }
                    }

                    if (TrayRegistry.SetPromoted(e.Id, 1))
                    {
                        changed++;
                        lastSeen[e.Id] = 1;
                        promotedAt[e.Id] = DateTime.UtcNow;
                        Log.Write("Promoted: {0} (was {1}) [{2}]", e.DisplayName, e.Promoted < 0 ? "absent" : e.Promoted.ToString(), e.Id);
                    }
                }

                if (changed > 0 || (reason != "watch" && reason != "timer")) Log.Write("Pass ({0}): {1} entries, {2} promoted now", reason, entries.Count, changed);
                if (exclusionsChanged)
                {
                    config.Save();
                    EventHandler h = ExclusionsChanged;
                    if (h != null) h(this, EventArgs.Empty);
                }
                return changed;
            }
        }

        public void Unexclude(string identity)
        {
            lock (sync)
            {
                if (config.Exclusions.Remove(identity)) config.Save();
                foreach (IconEntry e in TrayRegistry.ReadAll())
                    if (string.Equals(e.Identity, identity, StringComparison.OrdinalIgnoreCase) && e.Promoted != 1)
                    {
                        if (TrayRegistry.SetPromoted(e.Id, 1)) { lastSeen[e.Id] = 1; promotedAt[e.Id] = DateTime.UtcNow; }
                    }
            }
        }
    }

    // Background thread: waits on registry change notifications for the whole NotifyIconSettings tree.
    sealed class Watcher : IDisposable
    {
        readonly Promoter promoter;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly AutoResetEvent kick = new AutoResetEvent(false);
        Thread thread;

        public Watcher(Promoter promoter) { this.promoter = promoter; }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "TrayAlways watcher";
            thread.Start();
        }

        public void Kick() { kick.Set(); }

        void Run()
        {
            try { promoter.Pass("startup"); } catch (Exception e) { Log.Write("Startup pass failed: {0}", e); }
            while (!stop.WaitOne(0))
            {
                RegistryKey root = null;
                try
                {
                    root = Registry.CurrentUser.OpenSubKey(TrayRegistry.KeyPath, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey);
                    if (root == null)
                    {
                        Log.Write("{0} not found (Windows 11 22H2+ required); retrying in 60 s", TrayRegistry.KeyPath);
                        if (WaitHandle.WaitAny(new WaitHandle[] { stop, kick }, 60000) == 0) return;
                        continue;
                    }
                    using (AutoResetEvent changed = new AutoResetEvent(false))
                    {
                        while (!stop.WaitOne(0))
                        {
                            int rc = Native.RegNotifyChangeKeyValue(root.Handle, true,
                                Native.REG_NOTIFY_CHANGE_NAME | Native.REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true);
                            if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);

                            // The 5-minute timeout is only a safety net; changes arrive as events.
                            int which = WaitHandle.WaitAny(new WaitHandle[] { stop, changed, kick }, TimeSpan.FromMinutes(5));
                            if (which == 0) return;
                            string reason = which == 1 ? "watch" : (which == 2 ? "manual" : "timer");
                            if (which == 1) Thread.Sleep(250); // debounce: Explorer writes several values per icon
                            promoter.Pass(reason);
                            // Our own writes re-signal the (re-armed) event once; the next pass finds nothing to do.
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Write("Watcher error: {0}", e.Message);
                    if (stop.WaitOne(5000)) return;
                }
                finally { if (root != null) root.Close(); }
            }
        }

        public void Dispose()
        {
            stop.Set();
            if (thread != null) thread.Join(2000);
        }
    }

    static class ExplorerControl
    {
        public static void Restart()
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) { Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")); return; }
            uint pid;
            Native.GetWindowThreadProcessId(tray, out pid);
            Log.Write("Restarting Explorer (pid {0})", pid);
            try { Process.GetProcessById((int)pid).Kill(); } catch (Exception e) { Log.Write("Kill failed: {0}", e.Message); }
            // Winlogon restarts the shell automatically (AutoRestartShell=1); start it ourselves if it did not.
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(150);
                if (Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return;
            }
            Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        }
    }

    // Reads which tray icons are currently on the taskbar (not in the overflow) via UI Automation.
    static class TrayProbe
    {
        public static List<string> VisibleIconNames()
        {
            List<string> names = new List<string>();
            try
            {
                Type ae = Type.GetType("System.Windows.Automation.AutomationElement, UIAutomationClient, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                if (ae == null) return names;
                // Late-bound so the app starts even if UIAutomationClient is unavailable.
                object root = ae.GetProperty("RootElement").GetValue(null, null);
                Type pc = Type.GetType("System.Windows.Automation.PropertyCondition, UIAutomationClient, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                Type ts = Type.GetType("System.Windows.Automation.TreeScope, UIAutomationTypes, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                object classProp = ae.GetField("ClassNameProperty").GetValue(null);
                object idProp = ae.GetField("AutomationIdProperty").GetValue(null);
                object children = Enum.Parse(ts, "Children");
                object descendants = Enum.Parse(ts, "Descendants");
                object trayCond = Activator.CreateInstance(pc, classProp, "Shell_TrayWnd");
                object tray = ae.GetMethod("FindFirst").Invoke(root, new object[] { children, trayCond });
                if (tray == null) return names;
                object iconCond = Activator.CreateInstance(pc, idProp, "NotifyItemIcon");
                System.Collections.IEnumerable found = (System.Collections.IEnumerable)ae.GetMethod("FindAll").Invoke(tray, new object[] { descendants, iconCond });
                foreach (object el in found)
                {
                    object cur = ae.GetProperty("Current").GetValue(el, null);
                    string n = (string)cur.GetType().GetProperty("Name").GetValue(cur, null);
                    n = (n ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                    names.Add(n.Length == 0 ? "(no name)" : n);
                }
            }
            catch (Exception e) { names.Add("UIA error: " + e.Message); }
            return names;
        }
    }

    static class Diagnostics
    {
        public static string Run(Config config)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("TrayAlways " + Assembly.GetExecutingAssembly().GetName().Version + " diagnostics, " + DateTime.Now.ToString("u"));
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    sb.AppendLine(string.Format("Windows: {0} build {1}.{2}", k.GetValue("DisplayVersion"), k.GetValue("CurrentBuild"), k.GetValue("UBR")));
            }
            catch { }
            sb.AppendLine("Enabled=" + config.Enabled + " RespectManualHide=" + config.RespectManualHide + " Exclusions=" + config.Exclusions.Count);
            sb.AppendLine();
            sb.AppendLine("Visible on taskbar BEFORE: " + string.Join(" | ", TrayProbe.VisibleIconNames()));
            List<IconEntry> entries = TrayRegistry.ReadAll();
            sb.AppendLine();
            sb.AppendLine("Registry entries (" + entries.Count + "):");
            foreach (IconEntry e in entries.OrderBy(x => x.DisplayName))
                sb.AppendLine(string.Format("  {0,-6} {1,-40} {2}{3}", e.Promoted < 0 ? "absent" : e.Promoted.ToString(), Trunc(e.DisplayName, 40), e.Id,
                    config.Exclusions.Contains(e.Identity) ? "  [kept hidden]" : ""));
            int n = new Promoter(config).Pass("diag");
            sb.AppendLine();
            sb.AppendLine("Promoted in this pass: " + n);
            Thread.Sleep(2500);
            sb.AppendLine("Visible on taskbar AFTER 2.5 s: " + string.Join(" | ", TrayProbe.VisibleIconNames()));
            sb.AppendLine();
            sb.AppendLine("If an icon was promoted but is still not on the taskbar, Explorer did not pick up the change live;");
            sb.AppendLine("use 'Restart Explorer' from the TrayAlways menu (or run TrayAlways.exe --restart-explorer).");
            return sb.ToString();
        }

        static string Trunc(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }
    }

    static class AppIcon
    {
        // Blue taskbar strip with three icons on it.
        public static Bitmap Draw(int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 16f;
                RectangleF bar = new RectangleF(0.5f * s, 4.5f * s, 15f * s, 7f * s);
                using (GraphicsPath path = Rounded(bar, 2.2f * s))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 0, 120, 212)))
                    g.FillPath(fill, path);
                using (SolidBrush white = new SolidBrush(Color.White))
                {
                    float d = 3.2f * s, y = bar.Top + (bar.Height - d) / 2f;
                    for (int i = 0; i < 3; i++)
                        g.FillEllipse(white, bar.Left + 1.9f * s + i * 4.1f * s, y, d, d);
                }
            }
            return bmp;
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Icon ForTray()
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (Bitmap b = Draw(size))
            {
                IntPtr h = b.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon copy = (Icon)tmp.Clone();
                tmp.Dispose();
                Native.DestroyIcon(h);
                return copy;
            }
        }

        // Multi-size .ico with PNG payloads (used by build.cmd for the exe icon).
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int sz in sizes)
                using (Bitmap b = Draw(sz))
                using (MemoryStream ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); pngs.Add(ms.ToArray()); }
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }

    // Hidden window that hears "TaskbarCreated" (Explorer restarted), the "show yourself" ping
    // from a second launch, and the global show-icon shortcut.
    sealed class MessageWindow : NativeWindow, IDisposable
    {
        public static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        public static readonly uint ShowPing = Native.RegisterWindowMessage("TrayAlways.ShowMenu");
        const int HotkeyId = 1;
        bool hotkeyRegistered;
        public event EventHandler ExplorerRestarted;
        public event EventHandler Pinged;
        public event EventHandler HotkeyPressed;

        public MessageWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "TrayAlways.MessageWindow";
            CreateHandle(cp);
        }

        public bool RegisterHotkey(Hotkey h)
        {
            UnregisterHotkey();
            if (h == null || !h.IsValid) return false;
            hotkeyRegistered = Native.RegisterHotKey(Handle, HotkeyId, h.Modifiers | Native.MOD_NOREPEAT, (uint)h.Key);
            if (!hotkeyRegistered) Log.Write("Shortcut {0} is taken (error {1})", h, Marshal.GetLastWin32Error());
            return hotkeyRegistered;
        }

        public void UnregisterHotkey()
        {
            if (hotkeyRegistered) Native.UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
        }

        public bool HotkeyRegistered { get { return hotkeyRegistered; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)TaskbarCreated && ExplorerRestarted != null) ExplorerRestarted(this, EventArgs.Empty);
            else if (m.Msg == (int)ShowPing && Pinged != null) Pinged(this, EventArgs.Empty);
            else if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId && HotkeyPressed != null) HotkeyPressed(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose() { UnregisterHotkey(); DestroyHandle(); }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly Config config;
        readonly Promoter promoter;
        readonly Watcher watcher;
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly MessageWindow msgWindow;

        public TrayApp(Config config)
        {
            this.config = config;
            promoter = new Promoter(config);
            watcher = new Watcher(promoter);

            menu = new ContextMenuStrip();
            menu.Opening += delegate { BuildMenu(); };
            icon = new NotifyIcon();
            icon.Icon = AppIcon.ForTray();
            icon.Text = "TrayAlways";
            icon.ContextMenuStrip = menu;
            icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) OpenMenu(); // same menu on left click
            };

            msgWindow = new MessageWindow();
            msgWindow.ExplorerRestarted += delegate { Log.Write("Explorer restarted"); watcher.Kick(); };
            msgWindow.Pinged += delegate
            {
                bool wasHidden = !icon.Visible;
                ShowIcon();
                if (!wasHidden) icon.ShowBalloonTip(3000, "TrayAlways", L.T("already_running"), ToolTipIcon.Info);
            };
            msgWindow.HotkeyPressed += delegate
            {
                if (!icon.Visible) ShowIcon();
                OpenMenu();
            };

            bool hotkeyOk = msgWindow.RegisterHotkey(config.ShowHotkey);
            icon.Visible = !config.IconHidden || !hotkeyOk;
            if (config.IconHidden && !hotkeyOk)
            {
                // Never leave the user with an invisible app and no way back.
                config.IconHidden = false;
                config.Save();
                icon.ShowBalloonTip(8000, "TrayAlways", L.F("hotkey_taken_startup", config.ShowHotkey), ToolTipIcon.Warning);
            }

            watcher.Start();

            if (!config.FirstRunDone)
            {
                config.FirstRunDone = true;
                config.Save();
                icon.ShowBalloonTip(6000, "TrayAlways", L.T("first_run"), ToolTipIcon.Info);
            }
            Log.Write("TrayAlways {0} started, exe {1}, language {2}, icon {3}", Assembly.GetExecutingAssembly().GetName().Version,
                Application.ExecutablePath, L.Code, icon.Visible ? "shown" : "hidden");
        }

        void OpenMenu()
        {
            MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(icon, null);
        }

        void ShowIcon()
        {
            icon.Visible = true;
            if (config.IconHidden) { config.IconHidden = false; config.Save(); Log.Write("Icon shown"); }
        }

        void HideIcon()
        {
            if (!msgWindow.HotkeyRegistered && !msgWindow.RegisterHotkey(config.ShowHotkey))
            {
                MessageBox.Show(L.T("hotkey_taken"), "TrayAlways", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!config.HideHintShown)
            {
                MessageBox.Show(L.F("hide_hint", config.ShowHotkey), "TrayAlways", MessageBoxButtons.OK, MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1, L.Rtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : (MessageBoxOptions)0);
                config.HideHintShown = true;
            }
            icon.Visible = false;
            config.IconHidden = true;
            config.Save();
            Log.Write("Icon hidden; shortcut {0}", config.ShowHotkey);
        }

        void ChangeHotkey()
        {
            Hotkey old = config.ShowHotkey;
            msgWindow.UnregisterHotkey(); // otherwise pressing the current shortcut never reaches the dialog
            Hotkey chosen = null;
            using (HotkeyDialog d = new HotkeyDialog(old, delegate(Hotkey h) { if (msgWindow.RegisterHotkey(h)) { chosen = h; return true; } return false; }))
                d.ShowDialog();
            if (chosen != null)
            {
                config.ShowHotkey = chosen;
                config.Save();
                Log.Write("Shortcut changed to {0}", chosen);
            }
            else msgWindow.RegisterHotkey(old);
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            menu.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
            menu.Font = SystemFonts.MenuFont;
            List<IconEntry> entries = TrayRegistry.ReadAll();
            int shown = entries.Count(e => e.Promoted == 1);

            ToolStripMenuItem header = new ToolStripMenuItem(L.F("header", shown, entries.Count));
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem enabled = new ToolStripMenuItem(L.T("always_show"));
            enabled.Checked = config.Enabled;
            enabled.Click += delegate { config.Enabled = !config.Enabled; config.Save(); Log.Write("Enabled={0}", config.Enabled); if (config.Enabled) watcher.Kick(); };
            menu.Items.Add(enabled);

            ToolStripMenuItem respect = new ToolStripMenuItem(L.T("respect"));
            respect.Checked = config.RespectManualHide;
            respect.ToolTipText = L.T("respect_tip");
            respect.Click += delegate { config.RespectManualHide = !config.RespectManualHide; config.Save(); };
            menu.Items.Add(respect);

            ToolStripMenuItem hidden = new ToolStripMenuItem(L.F("kept_hidden", config.Exclusions.Count));
            if (config.Exclusions.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem(L.T("kept_none"));
                none.Enabled = false;
                hidden.DropDownItems.Add(none);
            }
            else
            {
                foreach (string ident in config.Exclusions.OrderBy(x => x))
                {
                    string id = ident;
                    IconEntry match = entries.FirstOrDefault(e => string.Equals(e.Identity, id, StringComparison.OrdinalIgnoreCase));
                    string label = match != null ? match.DisplayName : Path.GetFileName(id.Split('|')[0]);
                    ToolStripMenuItem it = new ToolStripMenuItem(L.F("show_item", label));
                    it.ToolTipText = id;
                    it.Click += delegate { promoter.Unexclude(id); };
                    hidden.DropDownItems.Add(it);
                }
            }
            menu.Items.Add(hidden);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem now = new ToolStripMenuItem(L.T("apply_now"));
            now.Click += delegate { watcher.Kick(); };
            menu.Items.Add(now);

            ToolStripMenuItem restart = new ToolStripMenuItem(L.T("restart_explorer"));
            restart.ToolTipText = L.T("restart_tip");
            restart.Click += delegate { ThreadPool.QueueUserWorkItem(delegate { ExplorerControl.Restart(); }); };
            menu.Items.Add(restart);

            ToolStripMenuItem settings = new ToolStripMenuItem(L.T("taskbar_settings"));
            settings.Click += delegate { try { Process.Start("ms-settings:taskbar"); } catch { } };
            menu.Items.Add(settings);

            ToolStripMenuItem diag = new ToolStripMenuItem(L.T("diagnostics"));
            diag.Click += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string report = Diagnostics.Run(config);
                    string path = Path.Combine(Log.Dir, "diagnostics.txt");
                    File.WriteAllText(path, report, Encoding.UTF8);
                    Process.Start("notepad.exe", "\"" + path + "\"");
                });
            };
            menu.Items.Add(diag);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem auto = new ToolStripMenuItem(L.T("autostart"));
            auto.Checked = Autostart.IsEnabled();
            auto.Click += delegate { try { Autostart.Set(!Autostart.IsEnabled()); } catch (Exception ex) { MessageBox.Show(ex.Message, "TrayAlways"); } };
            menu.Items.Add(auto);

            // "Language" keeps an English hint so nobody gets stuck in a language they can't read.
            ToolStripMenuItem lang = new ToolStripMenuItem(L.Code == "en" ? L.T("language") : L.T("language") + " (Language)");
            ToolStripMenuItem langAuto = new ToolStripMenuItem(L.F("language_auto", L.NativeNameOf(L.Detect())));
            langAuto.Checked = string.IsNullOrEmpty(config.Language);
            langAuto.Click += delegate { SetLanguage(""); };
            lang.DropDownItems.Add(langAuto);
            lang.DropDownItems.Add(new ToolStripSeparator());
            foreach (KeyValuePair<string, string> l in L.Available())
            {
                string code = l.Key;
                ToolStripMenuItem li = new ToolStripMenuItem(l.Value);
                li.Checked = string.Equals(config.Language, code, StringComparison.OrdinalIgnoreCase);
                li.Click += delegate { SetLanguage(code); };
                lang.DropDownItems.Add(li);
            }
            menu.Items.Add(lang);

            ToolStripMenuItem hide = new ToolStripMenuItem(L.F("hide_icon", config.ShowHotkey));
            hide.Click += delegate { HideIcon(); };
            menu.Items.Add(hide);

            ToolStripMenuItem hk = new ToolStripMenuItem(L.F("hotkey", config.ShowHotkey));
            hk.Click += delegate { ChangeHotkey(); };
            menu.Items.Add(hk);

            ToolStripMenuItem log = new ToolStripMenuItem(L.T("open_log"));
            log.Click += delegate { if (File.Exists(Log.FilePath)) Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); };
            menu.Items.Add(log);

            ToolStripMenuItem exit = new ToolStripMenuItem(L.T("exit"));
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);
        }

        void SetLanguage(string code)
        {
            config.Language = code;
            config.Save();
            L.Use(code);
            Log.Write("Language set to {0} ({1})", string.IsNullOrEmpty(code) ? "auto" : code, L.Code);
        }

        protected override void ExitThreadCore()
        {
            Log.Write("Exiting");
            watcher.Dispose();
            icon.Visible = false;
            icon.Dispose();
            msgWindow.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        const string Usage =
@"TrayAlways — keeps every Windows 11 tray icon visible on the taskbar.

  TrayAlways.exe                     run in the background (tray icon with menu)
  TrayAlways.exe --once              promote all icons once and exit
  TrayAlways.exe --status            list tray icon entries and their state
  TrayAlways.exe --diag              promote once and report what moved
  TrayAlways.exe --restart-explorer  restart Explorer so it re-reads all settings
  TrayAlways.exe --autostart on|off  start with Windows (HKCU Run key)
  TrayAlways.exe --write-icon <file> write the app icon as .ico (used by build)";

        [STAThread]
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (cmd.Length > 0)
            {
                Native.AttachConsole(-1);
                Log.Echo = Console.Out;
                Console.WriteLine();
                try { return RunCommand(cmd, args); }
                catch (Exception e) { Console.WriteLine("Error: " + e.Message); return 1; }
            }

            bool createdNew;
            using (Mutex single = new Mutex(true, @"Local\TrayAlways.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    IntPtr w = Native.FindWindow(null, "TrayAlways.MessageWindow");
                    if (w != IntPtr.Zero) PostMessage(w, MessageWindow.ShowPing, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write("UI error: {0}", e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Write("Fatal: {0}", e.ExceptionObject); };
                Config config = Config.Load();
                L.Use(config.Language);
                Application.Run(new TrayApp(config));
            }
            return 0;
        }

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static int RunCommand(string cmd, string[] args)
        {
            Config config = Config.Load();
            switch (cmd)
            {
                case "--once":
                    new Promoter(config).Pass("once");
                    return 0;
                case "--status":
                    foreach (IconEntry e in TrayRegistry.ReadAll().OrderBy(x => x.DisplayName))
                        Console.WriteLine("{0,-7}{1,-42}{2}{3}", e.Promoted == 1 ? "shown" : (e.Promoted == 0 ? "hidden" : "new"), e.DisplayName, e.ExePath,
                            config.Exclusions.Contains(e.Identity) ? "  [kept hidden]" : "");
                    Console.WriteLine();
                    Console.WriteLine("On taskbar now: " + string.Join(" | ", TrayProbe.VisibleIconNames()));
                    return 0;
                case "--diag":
                    Log.Echo = null;
                    Console.WriteLine(Diagnostics.Run(config));
                    return 0;
                case "--restart-explorer":
                    ExplorerControl.Restart();
                    return 0;
                case "--autostart":
                    Autostart.Set(args.Length > 1 && args[1].Equals("on", StringComparison.OrdinalIgnoreCase));
                    return 0;
                case "--check-languages":
                    if (args.Length > 1)
                    {
                        // e.g. --check-languages zh-TW pt-AO : show what each Windows language would get
                        for (int i = 1; i < args.Length; i++)
                        {
                            Thread.CurrentThread.CurrentUICulture = new CultureInfo(args[i]);
                            Console.WriteLine("{0,-12} -> {1}", args[i], L.Detect());
                        }
                        return 0;
                    }
                    return L.Check(Console.Out) == 0 ? 0 : 3;
                case "--write-icon":
                    AppIcon.WriteIco(args[1]);
                    return 0;
                default:
                    Console.WriteLine(Usage);
                    return cmd == "--help" || cmd == "-h" || cmd == "/?" ? 0 : 2;
            }
        }
    }
}
