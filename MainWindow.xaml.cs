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
        InitializeComponent(); AccountsList.ItemsSource = FilteredAccounts; LoadAccountsFromJson();
        foreach (var account in AllAccounts.Where(a => a.Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase))) UpdateSteamAccountDetails(account);
        DetectAndSaveActiveAccount("Steam"); RefreshFilteredList();
    }

    private void PlatformNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string platform) { _currentPlatform = platform; PlatformTitleText.Text = platform + " Accounts"; DetectAndSaveActiveAccount(platform); RefreshFilteredList(); }
    }
    private void RefreshFilteredList() { FilteredAccounts.Clear(); foreach (var acc in AllAccounts.Where(a => a.Platform == _currentPlatform)) FilteredAccounts.Add(acc); }
    private void DetectAndSaveActiveAccount(string platform)
    {
        string username = DetectAccountName(platform);
        if (string.IsNullOrWhiteSpace(username) || IsAlreadyAdded(platform, username)) return;
        var account = new Account { Username = username, DisplayName = username, Platform = platform, Note = "Main Account" };
        if (platform.Equals("Steam", StringComparison.OrdinalIgnoreCase)) UpdateSteamAccountDetails(account);
        AllAccounts.Add(account); SaveSessionSnapshot(account); SaveAccountsToJson();
    }
    private string DetectAccountName(string platform) => platform switch { "Steam" => GetSteamAccountName(), "Riot" => GetRiotAccountName(), "Epic" => GetEpicAccountName(), _ => "" };

    private string GetSteamAccountName()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"); string? autoLogin = key?.GetValue("AutoLoginUser")?.ToString();
            var entries = GetSteamEntries(); var selected = entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(autoLogin) && e.AccountName.Equals(autoLogin, StringComparison.OrdinalIgnoreCase)) ?? entries.FirstOrDefault(e => e.MostRecent);
            return selected?.AccountName ?? autoLogin ?? "";
        }
        catch { return ""; }
    }
    private List<SteamEntry> GetSteamEntries()
    {
        var entries = new List<SteamEntry>();
        try
        {
            string path = Path.Combine(GetSteamPath(), "config", "loginusers.vdf"); if (!File.Exists(path)) return entries;
            string text = File.ReadAllText(path); var blocks = Regex.Matches(text, "(?ms)^\\s*\\\"(?<id>\\d{15,20})\\\"\\s*\\{(?<body>.*?)^\\s*\\}");
            foreach (Match block in blocks)
            {
                string body = block.Groups["body"].Value; string? name = ReadVdfValue(body, "AccountName"); if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add(new SteamEntry(name, ReadVdfValue(body, "PersonaName") ?? name, ReadVdfValue(body, "MostRecent") == "1", block.Groups["id"].Value, ReadVdfValue(body, "AvatarUrl")));
            }
        }
        catch { }
        return entries;
    }
    private string GetSteamPath()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"); string? path = key?.GetValue("SteamPath")?.ToString() ?? key?.GetValue("InstallPath")?.ToString(); if (!string.IsNullOrWhiteSpace(path)) return path.Replace('/', Path.DirectorySeparatorChar); }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    }
    private void UpdateSteamAccountDetails(Account account)
    {
        try
        {
            var entry = GetSteamEntries().FirstOrDefault(e => e.AccountName.Equals(account.Username, StringComparison.OrdinalIgnoreCase)); if (entry == null) return;
            account.DisplayName = entry.PersonaName; string cache = Path.Combine(GetSteamPath(), "config", "avatarcache");
            string? image = Directory.Exists(cache) ? Directory.GetFiles(cache, entry.SteamId + ".*").Where(p => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            if (!string.IsNullOrWhiteSpace(image)) account.AvatarPath = image;
            else if (Uri.TryCreate(entry.AvatarUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) account.AvatarPath = uri.AbsoluteUri;
        }
        catch { }
    }
    private sealed record SteamEntry(string AccountName, string PersonaName, bool MostRecent, string SteamId, string? AvatarUrl);
    private static string? ReadVdfValue(string block, string key) { var m = Regex.Match(block, "(?im)^\\s*\\\"" + Regex.Escape(key) + "\\\"\\s*\\\"(?<value>[^\\\"]*)\\\""); return m.Success ? m.Groups["value"].Value : null; }

    private string GetRiotAccountName()
    {
        try
        {
            string data = GetRiotDataPath();
            foreach (string file in new[] { "RiotClientPrivateSettings.yaml", "RiotGamesPrivateSettings.yaml" })
                if (File.Exists(Path.Combine(data, file))) { string? found = MatchNamedValue(ReadFileShared(Path.Combine(data, file)), @"(?im)^\s*(?:game_name|riot_id|display_name|displayName|username|name)\s*:\s*[""']?([^\r\n""']+)"); if (Plausible(found)) return found!.Trim(); }
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Logs");
            if (Directory.Exists(logs)) foreach (string p in Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(20)) { string? found = MatchNamedValue(ReadFileShared(p), @"(?im)(?:riot\s*id|game_name|logged\s+in\s+as|account\s+name)\s*[:=]\s*[""']?([^\r\n""']{2,64})"); if (Plausible(found)) return found!.Trim(); }
        }
        catch { }
        return "";
    }
    private string GetEpicAccountName()
    {
        string saved = GetEpicSavedPath();
        foreach (string ini in new[] { Path.Combine(saved, "Config", "Windows", "GameUserSettings.ini"), Path.Combine(saved, "Config", "Windows", "GameUserSettings.ini") })
        {
            if (!File.Exists(ini)) continue;
            foreach (string key in new[] { "DisplayName", "LastLoginUserName", "AccountName" })
            {
                string? value = ReadIniValue(ini, key);
                if (Plausible(value)) return value!.Trim();
            }
        }
        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\EpicGamesLauncher");
            foreach (string key in new[] { "DisplayName", "LastLoginUserName", "AccountName" })
            {
                string? value = reg?.GetValue(key)?.ToString();
                if (Plausible(value)) return value!.Trim();
            }
        }
        catch { }

        // Launcher logs are actively written while Epic is open; share read access with its writer.
        string logs = Path.Combine(saved, "Logs");
        try
        {
            if (Directory.Exists(logs))
            {
                foreach (string path in Directory.GetFiles(logs, "*.log").OrderByDescending(SafeLastWriteTimeUtc).Take(20))
                {
                    string text;
                    try { text = ReadFileShared(path); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    foreach (string pattern in new[] {
                        @"(?im)User\s+(?<id>[0-9a-f]{32})\s*\((?<name>[^)\r\n]{2,64})\)\s*signed\s*in",
                        @"(?im)(?:logging\s+in\s+user|display\s*name|user)\s*[:=]\s*[""']?(?<name>[^\r\n""']{2,64})"
                    })
                    {
                        var match = Regex.Match(text, pattern);
                        if (match.Success && Plausible(match.Groups["name"].Value)) return match.Groups["name"].Value.Trim();
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "";
    }
    private static string ReadFileShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static DateTime SafeLastWriteTimeUtc(string path) { try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }
    private static bool Plausible(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 64 && !value.Contains("token", StringComparison.OrdinalIgnoreCase) && !value.Contains("puuid", StringComparison.OrdinalIgnoreCase);
    private static string? MatchNamedValue(string text, string pattern) { var m = Regex.Match(text, pattern); return m.Success ? m.Groups[1].Value.Trim().Trim('"', '\'') : null; }
    private static string? ReadIniValue(string path, string key) { try { foreach (string line in ReadFileShared(path).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)) { string t = line.Trim(); int i = t.IndexOf('='); if (i > 0 && t[..i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return t[(i + 1)..].Trim().Trim('"'); } } catch (IOException) { } catch (UnauthorizedAccessException) { } return null; }

    private bool IsAlreadyAdded(string platform, string username) => AllAccounts.Any(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) && a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        string username = DetectAccountName(_currentPlatform);
        if (string.IsNullOrWhiteSpace(username)) { MessageBox.Show("Please sign in to your " + _currentPlatform + " account first!", "Sign In Required", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (IsAlreadyAdded(_currentPlatform, username)) { MessageBox.Show("This account is already added!", "Notice", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var account = new Account { Username = username, DisplayName = username, Platform = _currentPlatform, Note = "Main Account" }; if (_currentPlatform == "Steam") UpdateSteamAccountDetails(account);
        AllAccounts.Add(account); SaveSessionSnapshot(account); SaveAccountsToJson(); RefreshFilteredList();
    }
    private void LoginNewAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlatform == "Steam") { KillProcesses("steam"); using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true); key?.SetValue("AutoLoginUser", ""); Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); }
            else if (_currentPlatform == "Riot") { KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "VALORANT", "LeagueClient", "LeagueClientUx"); StartRiotClient(); }
            else if (_currentPlatform == "Epic") { KillProcesses("EpicGamesLauncher", "EpicWebHelper"); StartEpicLauncher(); }
        }
        catch { }
    }
    private void DeleteAccount_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement el && el.DataContext is Account a) { AllAccounts.Remove(a); SaveAccountsToJson(); RefreshFilteredList(); } }
    private void SwitchAccount_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement el && el.DataContext is Account a) { if (a.Platform == "Steam") SwitchSteamAccount(a.Username); else if (a.Platform == "Riot") SwitchRiotAccount(a); else if (a.Platform == "Epic") SwitchEpicAccount(a); } }
    private void KillProcesses(params string[] names) { foreach (string n in names) foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); p.WaitForExit(3000); } catch { } finally { p.Dispose(); } } }
    private void SwitchSteamAccount(string username) { try { KillProcesses("steam"); using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true); if (key != null) { key.SetValue("AutoLoginUser", username); key.SetValue("RememberPassword", 1); } Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("Error switching account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void SwitchRiotAccount(Account a) { try { SaveSessionSnapshot(a); KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "VALORANT", "LeagueClient", "LeagueClientUx"); string from = Path.Combine(GetSessionPath("Riot", a.Username), "Data"); if (Directory.Exists(from)) CopyDirectory(from, GetRiotDataPath()); StartRiotClient(); } catch (Exception ex) { MessageBox.Show("Could not switch Riot account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void SwitchEpicAccount(Account a) { try { SaveSessionSnapshot(a); KillProcesses("EpicGamesLauncher", "EpicWebHelper"); string from = Path.Combine(GetSessionPath("Epic", a.Username), "Saved"); if (Directory.Exists(from)) CopyDirectory(from, GetEpicSavedPath()); StartEpicLauncher(); } catch (Exception ex) { MessageBox.Show("Could not switch Epic account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void SaveSessionSnapshot(Account a) { try { string target = GetSessionPath(a.Platform, a.Username); if (a.Platform == "Riot" && Directory.Exists(GetRiotDataPath())) CopyDirectory(GetRiotDataPath(), Path.Combine(target, "Data")); else if (a.Platform == "Epic" && Directory.Exists(GetEpicSavedPath())) CopyDirectory(GetEpicSavedPath(), Path.Combine(target, "Saved")); } catch { } }
    private string GetSessionPath(string platform, string username) => Path.Combine(_sessionsPath, platform, MakeSafeFileName(username));
    private static string MakeSafeFileName(string value) { foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return string.IsNullOrWhiteSpace(value) ? "UnknownAccount" : value; }
    private string GetRiotDataPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");
    private string GetEpicSavedPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved");
    private static void CopyDirectory(string src, string dst) { Directory.CreateDirectory(dst); foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); foreach (string d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d))); }
    private void StartRiotClient() { string[] paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "Riot Client", "RiotClientServices.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games", "Riot Client", "RiotClientServices.exe"), @"C:\Riot Games\Riot Client\RiotClientServices.exe" }; string? exe = paths.FirstOrDefault(File.Exists); Process.Start(new ProcessStartInfo(exe ?? "riotclient://") { UseShellExecute = true }); }
    private void StartEpicLauncher() { string[] paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe") }; string? exe = paths.FirstOrDefault(File.Exists); Process.Start(new ProcessStartInfo(exe ?? "com.epicgames.launcher://") { UseShellExecute = true }); }
    private void SaveNote_Click(object sender, RoutedEventArgs e) { SaveAccountsToJson(); MessageBox.Show("Note saved successfully!", "Willen Switcher", MessageBoxButton.OK, MessageBoxImage.Information); }
    private void LoadAccountsFromJson() { try { if (!File.Exists(_jsonPath)) return; var list = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_jsonPath)); if (list != null) { AllAccounts.Clear(); foreach (var a in list) { if (string.IsNullOrWhiteSpace(a.DisplayName)) a.DisplayName = a.Username; AllAccounts.Add(a); } } } catch { } }
    private void SaveAccountsToJson() { try { File.WriteAllText(_jsonPath, JsonSerializer.Serialize(AllAccounts.ToList(), new JsonSerializerOptions { WriteIndented = true })); } catch { } }
}

public class Account
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Platform { get; set; } = "Steam";
    public string Note { get; set; } = "";
    public string? AvatarPath { get; set; }
    [JsonIgnore] public ImageSource AvatarSource { get { try { if (!string.IsNullOrWhiteSpace(AvatarPath)) { if (Uri.TryCreate(AvatarPath, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) return new BitmapImage(uri); if (File.Exists(AvatarPath)) { var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(AvatarPath, UriKind.Absolute); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image; } } } catch { } string asset = Platform switch { "Steam" => "/Assets/steam.png", "Riot" => "/Assets/riot.png", "Epic" => "/Assets/epic.png", _ => "/Assets/steam.png" }; return new BitmapImage(new Uri("pack://application:,,," + asset, UriKind.Absolute)); } }
}
