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
            string settings = Path.Combine(data, "RiotClientPrivateSettings.yaml");
            if (File.Exists(settings))
            {
                string text = ReadFileShared(settings);
                string? gameName = MatchNamedValue(text, @"(?im)^\s*game_name\s*:\s*[""']?([^\r\n""']+)");
                string? tagLine = MatchNamedValue(text, @"(?im)^\s*tag_line\s*:\s*[""']?([^\r\n""']+)");
                if (Plausible(gameName) && Plausible(tagLine)) return gameName!.Trim() + "#" + tagLine!.Trim();
                string? display = MatchNamedValue(text, @"(?im)^\s*(?:display_name|displayName)\s*:\s*[""']?([^\r\n""']+)");
                if (Plausible(display)) return display!.Trim();
            }
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Logs", "Riot Client.log");
            if (File.Exists(log))
            {
                string text = ReadFileShared(log);
                string? gameName = MatchNamedValue(text, @"(?im)game_name\s*[:=]\s*[""']?([^\r\n,""']+)");
                string? tagLine = MatchNamedValue(text, @"(?im)tag_line\s*[:=]\s*[""']?([^\r\n,""']+)");
                if (Plausible(gameName) && Plausible(tagLine)) return gameName!.Trim() + "#" + tagLine!.Trim();
                string? display = MatchNamedValue(text, @"(?im)(?:display_name|displayName)\s*[:=]\s*[""']?([^\r\n,""']+)");
                if (Plausible(display)) return display!.Trim();
            }
        }
        catch { }
        return "";
    }

    // Treat launcher data as opaque credentials. Do not parse, log, or display tokens.
    // Epic's launcher profile location has varied across versions; probe both config directories.
    private string GetEpicAccountName()
    {
        string logs = Path.Combine(GetEpicSavedPath(), "Logs");
        try
        {
            if (!Directory.Exists(logs)) return "";
            foreach (string path in Directory.GetFiles(logs, "*.log", SearchOption.TopDirectoryOnly).OrderByDescending(SafeLastWriteTimeUtc).Take(30))
            {
                string text;
                try { text = ReadFileShared(path); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                var patterns = new[] { @"""(?:DisplayName|display_name|displayName)""\s*:\s*""(?<name>[^""]{1,64})""", @"(?:DisplayName|display_name|displayName|EpicAccountName)\s*[:=]\s*[""']?(?<name>[^\r\n,""']{1,64})", @"(?:logged\s+in\s+as|signed\s+in\s+as|user\s+name)\s*[:=]?\s*[""']?(?<name>[^\r\n,""']{1,64})", @"User\s+[0-9a-f]{32}\s*\((?<name>[^)\r\n]{1,64})\)\s*signed\s*in" };
                foreach (string pattern in patterns) { var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); for (int i = matches.Count - 1; i >= 0; i--) { string name = matches[i].Groups["name"].Value.Trim(); if (Plausible(name) && !LooksLikeEpicIdentifier(name)) return name; } }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return "";
    }
    private static bool LooksLikeEpicIdentifier(string value) => value.Length >= 32 && value.All(c => Uri.IsHexDigit(c) || c == '-');
    private static string ReadFileShared(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    private static DateTime SafeLastWriteTimeUtc(string path) { try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }
    private static bool Plausible(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 64 && !value.Contains("token", StringComparison.OrdinalIgnoreCase) && !value.Contains("puuid", StringComparison.OrdinalIgnoreCase) && !value.Contains("unknown", StringComparison.OrdinalIgnoreCase);
    private static string? MatchNamedValue(string text, string pattern) { var m = Regex.Match(text, pattern); return m.Success ? m.Groups[1].Value.Trim().Trim('"', '\'') : null; }

    private bool IsAlreadyAdded(string platform, string username) => AllAccounts.Any(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) && a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        string username = DetectAccountName(_currentPlatform);
        if (string.IsNullOrWhiteSpace(username) && _currentPlatform.Equals("Epic", StringComparison.OrdinalIgnoreCase))
        {
            string entered = Microsoft.VisualBasic.Interaction.InputBox("Epic's local launcher data does not reliably expose the signed-in display name. Enter the display name shown in the launcher; this labels the saved session and does not verify identity:", "Epic Account Name", "").Trim();
            if (!string.IsNullOrWhiteSpace(entered)) username = entered;
        }
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
            else if (_currentPlatform == "Riot")
            {
                KillRiotProcesses();
                string currentName = GetRiotAccountName();
                if (!string.IsNullOrWhiteSpace(currentName)) SaveRiotSessionSnapshot(currentName);
                ClearRiotActiveSession();
                StartRiotClient(true);
            }
            else if (_currentPlatform == "Epic") { KillProcesses("EpicGamesLauncher", "EpicWebHelper", "UnrealCEFSubProcess"); string activeName = GetEpicAccountName(); if (!string.IsNullOrWhiteSpace(activeName)) SaveEpicSessionSnapshot(activeName); ClearEpicActiveSession(); StartEpicLauncher(); }
        }
        catch (Exception ex) { MessageBox.Show("Could not start the login flow: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void DeleteAccount_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement el && el.DataContext is Account a) { AllAccounts.Remove(a); SaveAccountsToJson(); RefreshFilteredList(); } }
    private void SwitchAccount_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement el && el.DataContext is Account a) { if (a.Platform == "Steam") SwitchSteamAccount(a.Username); else if (a.Platform == "Riot") SwitchRiotAccount(a); else if (a.Platform == "Epic") SwitchEpicAccount(a); } }
    private void KillProcesses(params string[] names)
    {
        foreach (string n in names)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var processes = Process.GetProcessesByName(n);
                if (processes.Length == 0) break;
                foreach (var p in processes)
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { } finally { p.Dispose(); }
                }
                if (Process.GetProcessesByName(n).Length == 0) break;
            }
        }
    }
    private void KillRiotProcesses() => KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "Riot Client", "VALORANT", "LeagueClient", "LeagueClientUx");
    private void SwitchSteamAccount(string username) { try { KillProcesses("steam"); using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true); if (key != null) { key.SetValue("AutoLoginUser", username); key.SetValue("RememberPassword", 1); } Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("Error switching account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void SwitchRiotAccount(Account a)
    {
        try
        {
            KillRiotProcesses();
            string currentName = GetRiotAccountName();
            if (!string.IsNullOrWhiteSpace(currentName) && !currentName.Equals(a.Username, StringComparison.OrdinalIgnoreCase)) SaveRiotSessionSnapshot(currentName);
            ClearRiotActiveSession();
            string from = Path.Combine(GetSessionPath("Riot", a.Username), "Data");
            if (Directory.Exists(from)) CopyDirectory(from, GetRiotDataPath());
            StartRiotClient(false);
        }
        catch (Exception ex) { MessageBox.Show("Could not switch Riot account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void SaveRiotSessionSnapshot(string accountName)
    {
        string data = GetRiotDataPath(); if (!Directory.Exists(data)) return;
        string target = Path.Combine(GetSessionPath("Riot", accountName), "Data");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        CopyDirectory(data, target);
    }
    private void ClearRiotActiveSession()
    {
        string data = GetRiotDataPath();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { if (Directory.Exists(data)) Directory.Delete(data, true); return; }
            catch (IOException) { if (attempt == 4) throw; System.Threading.Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { if (attempt == 4) throw; System.Threading.Thread.Sleep(500); }
        }
    }
    private void SwitchEpicAccount(Account a)
    {
        try
        {
            KillProcesses("EpicGamesLauncher", "EpicWebHelper", "UnrealCEFSubProcess");
            string currentName = GetEpicAccountName();
            if (!string.IsNullOrWhiteSpace(currentName) && !currentName.Equals(a.Username, StringComparison.OrdinalIgnoreCase)) SaveEpicSessionSnapshot(currentName);
            RestoreEpicSession(a.Username);
            StartEpicLauncher();
        }
        catch (Exception ex) { MessageBox.Show("Could not switch Epic account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void SaveEpicSessionSnapshot(string accountName)
    {
        string saved = GetEpicSavedPath(); if (!Directory.Exists(saved)) return;
        string target = Path.Combine(GetSessionPath("Epic", accountName), "Saved");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        CopyDirectory(saved, target);
    }
    private void RestoreEpicSession(string accountName)
    {
        string saved = GetEpicSavedPath(); string target = Path.Combine(GetSessionPath("Epic", accountName), "Saved");
        if (!Directory.Exists(target)) { ClearEpicActiveSession(); return; }
        // Replace all launcher Saved data (including every versioned webcache) to avoid retaining the prior account.
        if (Directory.Exists(saved)) Directory.Delete(saved, true);
        CopyDirectory(target, saved);
    }
    private void ClearEpicActiveSession()
    {
        string saved = GetEpicSavedPath();
        foreach (string name in new[] { "Config", "Data", "webcache", "webcache_4147", "webcache_4430", "webcache_4431" })
        {
            string path = Path.Combine(saved, name);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); break; }
                catch (IOException) { if (attempt < 2) System.Threading.Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { if (attempt < 2) System.Threading.Thread.Sleep(500); }
            }
        }
        if (Directory.Exists(saved)) foreach (string path in Directory.GetDirectories(saved, "webcache*", SearchOption.TopDirectoryOnly))
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { Directory.Delete(path, true); break; }
                catch (IOException) { if (attempt < 2) System.Threading.Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { if (attempt < 2) System.Threading.Thread.Sleep(500); }
            }
        }
    }
    private void SaveSessionSnapshot(Account a) { try { string target = GetSessionPath(a.Platform, a.Username); if (a.Platform == "Riot" && Directory.Exists(GetRiotDataPath())) CopyDirectory(GetRiotDataPath(), Path.Combine(target, "Data")); else if (a.Platform == "Epic" && Directory.Exists(GetEpicSavedPath())) { string dest = Path.Combine(target, "Saved"); if (Directory.Exists(dest)) Directory.Delete(dest, true); CopyDirectory(GetEpicSavedPath(), dest); } } catch { } }
    private string GetSessionPath(string platform, string username) => Path.Combine(_sessionsPath, platform, MakeSafeFileName(username));
    private static string MakeSafeFileName(string value) { foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return string.IsNullOrWhiteSpace(value) ? "UnknownAccount" : value; }
    private string GetRiotDataPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");
    private string GetEpicSavedPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved");
    private static void CopyDirectory(string src, string dst) { Directory.CreateDirectory(dst); foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); foreach (string d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d))); }
    private void StartRiotClient(bool forceLogin)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "Riot Client", "RiotClientServices.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games", "Riot Client", "RiotClientServices.exe"), @"C:\Riot Games\Riot Client\RiotClientServices.exe", Path.Combine(local, "Riot Games", "Riot Client", "RiotClientServices.exe") };
        string? exe = paths.FirstOrDefault(File.Exists);
        if (exe != null)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (forceLogin) start.Arguments = "--launch-product=riot_client --launch-patchline=live";
            Process.Start(start);
        }
        else Process.Start(new ProcessStartInfo("riotclient://") { UseShellExecute = true });
    }
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
