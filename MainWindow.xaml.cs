using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WillenSwitcher
{
    public partial class MainWindow : Window
    {
        private string _currentPlatform = "Steam";
        private readonly string _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accounts.json");
        private readonly string _sessionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedSessions");
        public List<Account> AllAccounts { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            LoadAccountsFromJson();
            RefreshAccounts();
        }

        private void RefreshAccounts()
        {
            PlatformTitleText.Text = _currentPlatform + " Accounts";
            AccountsList.ItemsSource = null;
            AccountsList.ItemsSource = AllAccounts.Where(a => string.Equals(a.Platform, _currentPlatform, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void PlatformNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string platform }) { _currentPlatform = platform; RefreshAccounts(); }
        }

        private void AddAccount_Click(object sender, RoutedEventArgs e)
        {
            string username;
            if (_currentPlatform == "Riot")
            {
                username = FindRiotAccountName();
                if (string.IsNullOrWhiteSpace(username)) { MessageBox.Show("Could not detect a Riot ID. Sign in to Riot Client first.", "Willen Switcher"); return; }
            }
            else
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select an avatar image (Cancel to enter account name)", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*", CheckFileExists = true };
                string? avatar = dialog.ShowDialog() == true ? dialog.FileName : null;
                username = PromptForAccountName();
                if (string.IsNullOrWhiteSpace(username)) return;
                AddNewAccount(username, avatar);
                return;
            }
            AddNewAccount(username);
        }

        private string PromptForAccountName()
        {
            var window = new Window { Title = "Add account", Width = 360, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(22,25,34)) };
            var panel = new StackPanel { Margin = new Thickness(16) };
            var input = new TextBox { Margin = new Thickness(0,0,0,12), Padding = new Thickness(6) };
            var button = new Button { Content = "Add", Width = 70, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            button.Click += (_, _) => window.DialogResult = true;
            panel.Children.Add(new TextBlock { Text = "Account name", Foreground = Brushes.White, Margin = new Thickness(0,0,0,8) }); panel.Children.Add(input); panel.Children.Add(button); window.Content = panel;
            return window.ShowDialog() == true ? input.Text.Trim() : "";
        }

        private void AddNewAccount(string username, string? avatarPath = null)
        {
            if (AllAccounts.Any(a => a.Platform == _currentPlatform && string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("This account is already saved.", "Willen Switcher"); return; }
            AllAccounts.Add(new Account { Username = username, DisplayName = username, Platform = _currentPlatform, AvatarPath = avatarPath });
            SaveAccountsToJson(); RefreshAccounts();
        }

        private void DeleteAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Account account } && MessageBox.Show($"Delete {account.DisplayName}?", "Willen Switcher", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            { AllAccounts.Remove(account); SaveAccountsToJson(); RefreshAccounts(); }
        }

        private void SaveNote_Click(object sender, RoutedEventArgs e)
        {
            SaveAccountsToJson();
            MessageBox.Show("Note saved successfully!", "Willen Switcher", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoginNewAccount_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentSession();
            if (_currentPlatform == "Steam") { KillProcesses("steam"); StartSteam(); }
            else if (_currentPlatform == "Riot") { KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "LeagueClient", "VALORANT"); ClearDirectory(GetRiotDataPath()); StartRiotClient(true); }
            else { KillProcesses("EpicGamesLauncher", "EpicWebHelper"); ClearDirectory(Path.Combine(GetEpicSavedPath(), "webcache")); StartEpicLauncher(); }
        }

        private void SwitchAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Account account }) return;
            SaveCurrentSession();
            if (account.Platform == "Riot") { KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "LeagueClient", "VALORANT"); RestoreDirectory(Path.Combine(GetSessionPath(account.Platform, account.Username), "Data"), GetRiotDataPath()); StartRiotClient(false); }
            else if (account.Platform == "Epic") { KillProcesses("EpicGamesLauncher", "EpicWebHelper"); RestoreDirectory(Path.Combine(GetSessionPath(account.Platform, account.Username), "Saved"), GetEpicSavedPath()); StartEpicLauncher(); }
            else { SetSteamAccount(account.Username); StartSteam(); }
        }

        private void SaveCurrentSession()
        {
            if (_currentPlatform == "Riot") { string id = FindRiotAccountName(); if (!string.IsNullOrWhiteSpace(id)) SaveSessionSnapshot(AllAccounts.FirstOrDefault(a => a.Platform == "Riot" && a.Username.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? new Account { Platform = "Riot", Username = id }); }
            else if (_currentPlatform == "Epic") SaveSessionSnapshot(AllAccounts.FirstOrDefault(a => a.Platform == "Epic") ?? new Account { Platform = "Epic", Username = "Current" });
        }

        private static void KillProcesses(params string[] names)
        {
            foreach (var name in names) try { foreach (var process in Process.GetProcessesByName(name)) { using (process) { process.Kill(true); process.WaitForExit(5000); } } } catch { }
        }
        private static void ClearDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); Directory.CreateDirectory(path); } catch { } }
        private static void RestoreDirectory(string source, string destination) { try { if (!Directory.Exists(source)) return; if (Directory.Exists(destination)) Directory.Delete(destination, true); CopyDirectory(source, destination); } catch { } }
        private void SaveSessionSnapshot(Account account)
        {
            try { string target = GetSessionPath(account.Platform, account.Username); if (account.Platform == "Riot" && Directory.Exists(GetRiotDataPath())) { string dest = Path.Combine(target, "Data"); if (Directory.Exists(dest)) Directory.Delete(dest, true); CopyDirectory(GetRiotDataPath(), dest); } else if (account.Platform == "Epic" && Directory.Exists(GetEpicSavedPath())) { string dest = Path.Combine(target, "Saved"); if (Directory.Exists(dest)) Directory.Delete(dest, true); CopyDirectory(GetEpicSavedPath(), dest); } } catch { }
        }
        private string GetSessionPath(string platform, string username) => Path.Combine(_sessionsPath, platform, MakeSafeFileName(username));
        private static string MakeSafeFileName(string value) { foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return string.IsNullOrWhiteSpace(value) ? "UnknownAccount" : value; }
        private string GetRiotDataPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");
        private string GetEpicSavedPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved");
        private static void CopyDirectory(string src, string dst) { Directory.CreateDirectory(dst); foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); foreach (string d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d))); }

        private static string ReadFileShared(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
        private string FindRiotAccountName()
        {
            string[] roots = { GetRiotDataPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config") };
            foreach (string root in roots.Where(Directory.Exists))
            {
                IEnumerable<string> files; try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(5000).ToArray(); } catch { continue; }
                foreach (string path in files)
                {
                    string text; try { if (new FileInfo(path).Length > 2_000_000) continue; text = ReadFileShared(path); } catch { continue; }
                    string? game = Capture(text, "[\\\"']game_name[\\\"']\\s*:\\s*[\\\"']([^\\\"']+)");
                    string? tag = Capture(text, "[\\\"']tag_line[\\\"']\\s*:\\s*[\\\"']([^\\\"']+)");
                    if (!string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(tag)) return game.Trim() + "#" + tag.Trim();
                    string? logged = Capture(text, "(?im)Riot ID:\\s*([^\\r\\n]+)") ?? Capture(text, "(?im)Logged in as\\s+([^\\r\\n,]+)");
                    if (!string.IsNullOrWhiteSpace(logged) && logged.Length < 100) return logged.Trim();
                }
            }
            return "";
        }
        private static string? Capture(string text, string pattern) { var m = Regex.Match(text, pattern); return m.Success ? m.Groups[1].Value : null; }

        private static void StartSteam()
        {
            string[] paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "Steam.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "Steam.exe") };
            string? exe = paths.FirstOrDefault(File.Exists); Process.Start(new ProcessStartInfo(exe ?? "steam://open/main") { UseShellExecute = true });
        }
        private static void SetSteamAccount(string username)
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true); key?.SetValue("AutoLoginUser", username, RegistryValueKind.String); } catch { }
        }
        private static void StartRiotClient(bool forceLogin)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "Riot Client", "RiotClientServices.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games", "Riot Client", "RiotClientServices.exe"), @"C:\Riot Games\Riot Client\RiotClientServices.exe", Path.Combine(local, "Riot Games", "Riot Client", "RiotClientServices.exe") };
            string? exe = paths.FirstOrDefault(File.Exists); if (exe != null) { var info = new ProcessStartInfo(exe) { UseShellExecute = false, Arguments = forceLogin ? "--launch-product=riot_client --launch-patchline=live" : "" }; Process.Start(info); } else Process.Start(new ProcessStartInfo("riotclient://") { UseShellExecute = true });
        }
        private static void StartEpicLauncher()
        {
            string[] paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe") };
            string? exe = paths.FirstOrDefault(File.Exists); Process.Start(new ProcessStartInfo(exe ?? "com.epicgames.launcher://") { UseShellExecute = true });
        }
        private void LoadAccountsFromJson()
        {
            try { if (!File.Exists(_jsonPath)) return; var list = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_jsonPath)); if (list == null) return; AllAccounts.Clear(); foreach (var account in list) { if (string.IsNullOrWhiteSpace(account.DisplayName)) account.DisplayName = account.Username; AllAccounts.Add(account); } } catch { }
        }
        private void SaveAccountsToJson() { try { File.WriteAllText(_jsonPath, JsonSerializer.Serialize(AllAccounts, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) { MessageBox.Show("Could not save accounts: " + ex.Message, "Willen Switcher"); } }
    }
}

public class Account
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Platform { get; set; } = "Steam";
    public string Note { get; set; } = "";
    public string? AvatarPath { get; set; }
    [JsonIgnore]
    public ImageSource AvatarSource
    {
        get
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(AvatarPath))
                {
                    if (Uri.TryCreate(AvatarPath, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) return new BitmapImage(uri);
                    if (File.Exists(AvatarPath)) { var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(AvatarPath, UriKind.Absolute); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image; }
                }
            }
            catch { }
            string asset = Platform switch { "Steam" => "/Assets/steam.png", "Riot" => "/Assets/riot.png", "Epic" => "/Assets/epic.png", _ => "/Assets/steam.png" };
            return new BitmapImage(new Uri("pack://application:,,," + asset, UriKind.Absolute));
        }
    }
}
