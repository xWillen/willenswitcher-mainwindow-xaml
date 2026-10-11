using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

namespace WillenSwitcher;

public partial class MainWindow : Window
{
    public ObservableCollection<Account> AllAccounts { get; set; } = new();
    public ObservableCollection<Account> FilteredAccounts { get; set; } = new();
    private string _currentPlatform = "Steam";
    private readonly string _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accounts.json");
    private readonly string _sessionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedSessions");

    public MainWindow()
    {
        InitializeComponent();
        AccountsList.ItemsSource = FilteredAccounts;
        LoadAccountsFromJson();
        foreach (var account in AllAccounts.Where(a => a.Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase))) UpdateSteamAvatar(account);
        DetectAndSaveActiveAccount("Steam");
        RefreshFilteredList();
    }

    private void PlatformNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string platform)
        {
            _currentPlatform = platform;
            PlatformTitleText.Text = platform + " Accounts";
            if (platform.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                foreach (var account in AllAccounts.Where(a => a.Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase))) UpdateSteamAvatar(account);
            DetectAndSaveActiveAccount(platform);
            RefreshFilteredList();
        }
    }

    private void RefreshFilteredList()
    {
        FilteredAccounts.Clear();
        foreach (var acc in AllAccounts.Where(a => a.Platform == _currentPlatform)) FilteredAccounts.Add(acc);
    }

    private void DetectAndSaveActiveAccount(string platform)
    {
        string username = DetectAccountName(platform);
        if (string.IsNullOrWhiteSpace(username) || IsAlreadyAdded(platform, username)) return;
        var account = new Account { Username = username, Platform = platform, Note = "Main Account" };
        if (platform.Equals("Steam", StringComparison.OrdinalIgnoreCase)) UpdateSteamAvatar(account);
        AllAccounts.Add(account);
        SaveSessionSnapshot(account);
        SaveAccountsToJson();
        if (platform == _currentPlatform) RefreshFilteredList();
    }

    private string DetectAccountName(string platform) => platform switch
    {
        "Steam" => GetSteamAccountName(), "Riot" => GetRiotAccountName(), "Epic" => GetEpicAccountName(), _ => ""
    };

    private string GetSteamAccountName()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"); return key?.GetValue("AutoLoginUser")?.ToString() ?? ""; }
        catch { return ""; }
    }

    private string GetSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? path = key?.GetValue("SteamPath")?.ToString() ?? key?.GetValue("InstallPath")?.ToString();
            if (!string.IsNullOrWhiteSpace(path)) return path.Replace('/', Path.DirectorySeparatorChar);
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    }

    private void UpdateSteamAvatar(Account account)
    {
        try
        {
            string steamPath = GetSteamPath();
            string loginUsers = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsers)) return;
            string vdf = File.ReadAllText(loginUsers);
            var blocks = Regex.Matches(vdf, "(?ms)^\\s*\\\"(?<id>\\d{15,20})\\\"\\s*\\{(?<body>.*?)^\\s*\\}");
            foreach (Match block in blocks)
            {
                string body = block.Groups["body"].Value;
                if (!string.Equals(ReadVdfValue(body, "AccountName"), account.Username, StringComparison.OrdinalIgnoreCase)) continue;
                string steamId = block.Groups["id"].Value;
                string cache = Path.Combine(steamPath, "config", "avatarcache");
                string? imagePath = Directory.Exists(cache)
                    ? Directory.GetFiles(cache, steamId + ".*", SearchOption.TopDirectoryOnly)
                        .Where(p => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null;
                if (!string.IsNullOrWhiteSpace(imagePath)) { account.AvatarPath = imagePath; return; }
                string? url = ReadVdfValue(body, "AvatarUrl");
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)) account.AvatarPath = uri.AbsoluteUri;
                return;
            }
        }
        catch { }
    }

    private static string? ReadVdfValue(string block, string key)
    {
        var match = Regex.Match(block, "(?im)^\\s*\\\"" + Regex.Escape(key) + "\\\"\\s*\\\"(?<value>[^\\\"]*)\\\"");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private string GetRiotAccountName()
    {
        try
        {
            string dataPath = GetRiotDataPath();
            foreach (string file in new[] { "RiotGamesPrivateSettings.yaml", "RiotClientPrivateSettings.yaml" })
            {
                string path = Path.Combine(dataPath, file);
                if (!File.Exists(path)) continue;
                string text = File.ReadAllText(path);
                string? value = MatchNamedValue(text, @"(?im)^\s*(?:username|display_name|displayName|name)\s*:\s*[""']?([^\r\n""']+)");
                if (!string.IsNullOrWhiteSpace(value)) return value;
                value = MatchNamedValue(text, @"(?im)^\s*sub\s*:\s*[""']?([^\r\n""']+)");
                if (!string.IsNullOrWhiteSpace(value) && value.Contains('@')) return value;
            }
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Logs");
            if (Directory.Exists(logs))
                foreach (string path in Directory.GetFiles(logs, "Riot Client.log", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
                {
                    string? value = MatchNamedValue(File.ReadAllText(path), @"(?im)(?:logged\s+in\s+as|account)\s*[:=]?\s*([\w.@-]+)");
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
        }
        catch { }
        return "";
    }

    private static string? MatchNamedValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"', '\'') : null;
    }

    private string GetEpicAccountName()
    {
        try
        {
            string configPath = Path.Combine(GetEpicSavedPath(), "Config", "Windows", "GameUserSettings.ini");
            if (File.Exists(configPath))
            {
                string? value = ReadIniValue(configPath, "LastLoginUserName") ?? ReadIniValue(configPath, "DisplayName");
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\EpicGamesLauncher");
            foreach (string name in new[] { "LastLoginUserName", "DisplayName", "AccountName" })
            {
                string? value = key?.GetValue(name)?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch { }
        return "";
    }

    private static string? ReadIniValue(string path, string key)
    {
        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim(); int equals = trimmed.IndexOf('=');
            if (equals > 0 && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return trimmed[(equals + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private bool IsAlreadyAdded(string platform, string username) => AllAccounts.Any(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) && a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        string username = DetectAccountName(_currentPlatform);
        if (string.IsNullOrWhiteSpace(username)) { MessageBox.Show("Please sign in to your " + _currentPlatform + " account first!", "Sign In Required", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (IsAlreadyAdded(_currentPlatform, username)) { MessageBox.Show("This account is already added!", "Notice", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var account = new Account { Username = username, Platform = _currentPlatform, Note = "Main Account" };
        if (_currentPlatform.Equals("Steam", StringComparison.OrdinalIgnoreCase)) UpdateSteamAvatar(account);
        AllAccounts.Add(account); SaveSessionSnapshot(account); SaveAccountsToJson(); RefreshFilteredList();
    }

    private void LoginNewAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlatform == "Steam") { KillProcesses("steam"); using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true)) key?.SetValue("AutoLoginUser", ""); Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); }
            else if (_currentPlatform == "Riot") { KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "VALORANT", "LeagueClient", "LeagueClientUx"); string activeSettings = FindRiotSettingsFile(); if (!string.IsNullOrEmpty(activeSettings)) File.Move(activeSettings, activeSettings + ".willenswitcher-backup", true); StartRiotClient(); }
            else if (_currentPlatform == "Epic") { KillProcesses("EpicGamesLauncher"); StartEpicLauncher(); }
        }
        catch (Exception ex) { MessageBox.Show("Could not start the login flow: " + ex.Message, "Login Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Account acc) { AllAccounts.Remove(acc); SaveAccountsToJson(); RefreshFilteredList(); }
    }

    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Account acc)
        {
            if (acc.Platform == "Steam") SwitchSteamAccount(acc.Username);
            else if (acc.Platform == "Riot") SwitchRiotAccount(acc);
            else if (acc.Platform == "Epic") SwitchEpicAccount(acc);
        }
    }

    private void KillProcesses(params string[] processNames)
    {
        foreach (string processName in processNames) foreach (var process in Process.GetProcessesByName(processName))
        { try { process.Kill(); process.WaitForExit(3000); } catch { } finally { process.Dispose(); } }
    }

    private void SwitchSteamAccount(string username)
    {
        try { KillProcesses("steam"); using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true)) { if (key != null) { key.SetValue("AutoLoginUser", username); key.SetValue("RememberPassword", 1); } } Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Error switching account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SwitchRiotAccount(Account account)
    {
        try { SaveSessionSnapshot(account); KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "VALORANT", "LeagueClient", "LeagueClientUx"); string backupPath = GetSessionPath("Riot", account.Username); string dataPath = GetRiotDataPath(); if (Directory.Exists(Path.Combine(backupPath, "Data"))) CopyDirectory(Path.Combine(backupPath, "Data"), dataPath); StartRiotClient(); }
        catch (Exception ex) { MessageBox.Show("Could not switch Riot account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SwitchEpicAccount(Account account)
    {
        try { SaveSessionSnapshot(account); KillProcesses("EpicGamesLauncher"); string backupPath = GetSessionPath("Epic", account.Username); string savedPath = GetEpicSavedPath(); if (Directory.Exists(Path.Combine(backupPath, "Saved"))) CopyDirectory(Path.Combine(backupPath, "Saved"), savedPath); StartEpicLauncher(); }
        catch (Exception ex) { MessageBox.Show("Could not switch Epic Games account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveSessionSnapshot(Account account)
    {
        try
        {
            string platformPath = GetSessionPath(account.Platform, account.Username);
            if (account.Platform == "Riot") { string dataPath = GetRiotDataPath(); if (Directory.Exists(dataPath)) CopyDirectory(dataPath, Path.Combine(platformPath, "Data")); }
            else if (account.Platform == "Epic") { string savedPath = GetEpicSavedPath(); if (Directory.Exists(savedPath)) CopyDirectory(savedPath, Path.Combine(platformPath, "Saved")); }
        }
        catch { }
    }

    private string GetSessionPath(string platform, string username) => Path.Combine(_sessionsPath, platform, MakeSafeFileName(username));
    private static string MakeSafeFileName(string value) { foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_'); return string.IsNullOrWhiteSpace(value) ? "UnknownAccount" : value; }
    private string GetRiotDataPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");
    private string GetEpicSavedPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved");

    private string FindRiotSettingsFile()
    {
        string dataPath = GetRiotDataPath();
        foreach (string name in new[] { "RiotGamesPrivateSettings.yaml", "RiotClientPrivateSettings.yaml" }) { string candidate = Path.Combine(dataPath, name); if (File.Exists(candidate)) return candidate; }
        return "";
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private void StartRiotClient()
    {
        string[] candidates = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "Riot Client", "RiotClientServices.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games", "Riot Client", "RiotClientServices.exe"), @"C:\Riot Games\Riot Client\RiotClientServices.exe" };
        string? executable = candidates.FirstOrDefault(File.Exists); Process.Start(new ProcessStartInfo(executable ?? "riotclient://") { UseShellExecute = true });
    }

    private void StartEpicLauncher()
    {
        string[] candidates = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe") };
        string? executable = candidates.FirstOrDefault(File.Exists); Process.Start(new ProcessStartInfo(executable ?? "com.epicgames.launcher://") { UseShellExecute = true });
    }

    private void SaveNote_Click(object sender, RoutedEventArgs e) { SaveAccountsToJson(); MessageBox.Show("Note saved successfully!", "Willen Switcher", MessageBoxButton.OK, MessageBoxImage.Information); }

    private void LoadAccountsFromJson()
    {
        try { if (!File.Exists(_jsonPath)) return; var list = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_jsonPath)); if (list == null) return; AllAccounts.Clear(); foreach (var item in list) AllAccounts.Add(item); }
        catch { }
    }

    private void SaveAccountsToJson()
    {
        try { File.WriteAllText(_jsonPath, JsonSerializer.Serialize(AllAccounts.ToList(), new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}

public class Account
{
    public string Username { get; set; } = "";
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
                    if (Uri.TryCreate(AvatarPath, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return new BitmapImage(uri);
                    if (File.Exists(AvatarPath)) { var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(AvatarPath, UriKind.Absolute); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image; }
                }
            }
            catch { }
            string asset = Platform switch { "Steam" => "/Assets/steam.png", "Riot" => "/Assets/riot.png", "Epic" => "/Assets/epic.png", _ => "/Assets/steam.png" };
            return new BitmapImage(new Uri("pack://application:,,," + asset, UriKind.Absolute));
        }
    }
}
