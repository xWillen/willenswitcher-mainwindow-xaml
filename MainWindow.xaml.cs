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
            string text = File.ReadAllText(path); var blocks = Regex.Matches(text, "(?ms)^\\s*\\\"(?<id>\\d{15,20})\\\"\\s*\\{(?<body>.*?^)\\s*\\}");
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
    private string? ReadVdfValue(string block, string key) { var m = Regex.Match(block, "(?im)^\\s*\\\"" + Regex.Escape(key) + "\\\"\\s*\\\"(?<value>[^\\\"]*)\\\""); return m.Success ? m.Groups["value"].Value : null; }

    private string GetRiotAccountName()
    {
        string data = GetRiotDataPath();
        string root = Path.GetDirectoryName(data)!;
        string logs = Path.Combine(root, "Logs");
        string config = Path.Combine(root, "Config");
        var files = new List<string>();
        try
        {
            if (Directory.Exists(data))
            {
                string privateSettings = Path.Combine(data, "RiotClientPrivateSettings.yaml");
                if (File.Exists(privateSettings)) files.Add(privateSettings);
                files.AddRange(Directory.GetFiles(data, "*.yaml", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(data, "*.json", SearchOption.AllDirectories));
            }
            if (Directory.Exists(config)) files.AddRange(Directory.GetFiles(config, "*", SearchOption.AllDirectories).Where(File.Exists));
            if (Directory.Exists(logs)) files.AddRange(Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories));
            foreach (string path in files.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.EndsWith("RiotClientPrivateSettings.yaml", StringComparison.OrdinalIgnoreCase)).ThenByDescending(SafeLastWriteTimeUtc))
            {
                string text;
                try { text = ReadFileShared(path); } catch { continue; }
                string? gameName = MatchNamedValue(text, @"""game_name""\s*:\s*""([^""]+)""") ?? MatchNamedValue(text, @"(?im)game_name:\s*""?([^""\r\n]+)");
                string? tagLine = MatchNamedValue(text, @"""tag_line""\s*:\s*""([^""]+)""") ?? MatchNamedValue(text, @"(?im)tag_line:\s*""?([^""\r\n]+)");
                if (Plausible(gameName) && Plausible(tagLine)) return gameName!.Trim() + "#" + tagLine!.Trim();
                string? display = MatchNamedValue(text, @"(?im)displayName:\s*""?([^""\r\n]+)") ?? MatchNamedValue(text, @"""game_name""\s*:\s*""([^""]+)""");
                if (Plausible(display)) return display!.Trim();
                if (path.Contains(Path.DirectorySeparatorChar + "Logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    string? logged = MatchNamedValue(text, @"(?im)display name update received:\s*([^\r\n,]+)") ?? MatchNamedValue(text, @"(?im)Logged in as\s+([^\r\n,]+)") ?? MatchNamedValue(text, @"(?im)Riot ID:\s*([^\r\n,]+)") ?? MatchNamedValue(text, @"(?im)Player\s+([^\r\n#]+#[A-Za-z0-9]+)");
                    if (Plausible(logged)) return CleanRiotDisplayName(logged!);
                }
            }
        }
        catch { }
        return "";
    }

    private static string CleanRiotDisplayName(string value)
    {
        string cleaned = value.Trim();
        const string marker = "display name update received:";
        int markerIndex = cleaned.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0) cleaned = cleaned.Substring(markerIndex + marker.Length).Trim();
        cleaned = cleaned.Trim('"', '\'');
        int colon = cleaned.LastIndexOf(':');
        if (colon >= 0 && !cleaned.Contains('#')) cleaned = cleaned.Substring(colon + 1).Trim();
        return cleaned;
    }

    private string GetEpicAccountName()
    {
        var files = new List<string>();
        var roots = GetEpicProfileDirectories().Concat(new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Epic", "EpicGamesLauncher")
        }).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Where(Directory.Exists))
        {
            try { files.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => new[] { ".log", ".ini", ".json" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var patterns = new[]
        {
            @"""(?:DisplayName|display_name|displayName)""\s*:\s*""(?<name>[^\r\n,""']{1,64})""",
            @"(?:DisplayName|display_name|displayName|EpicAccountName)\s*[:=]\s*[""']?(?<name>[^\r\n,""']{1,64})",
            @"(?i)(?:logged\s+in\s+as|signed\s+in\s+as|user\s+name|display\s*name)\s*[:=]?\s*[""']?(?<name>[^\r\n,""']{1,64})",
            @"User\s+[0-9a-fA-F]{32}\s+\((?<name>[^\r\n,""']{1,64})\)\s+signed\s*in",
            @"UserId\s+[0-9a-fA-F]+\s+has\s+display\s+name\s+'(?<name>[^'\r\n]{1,64})'",
            @"DisplayName\s*\[(?<name>[^\]\r\n]{1,64})\]",
            @"(?i)(?:logged\s+in\s+as|signed\s+in\s+as)\s+\[(?<name>[^\]\r\n]{1,64})\]"
        };
        foreach (string path in files.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(SafeLastWriteTimeUtc))
        {
            string text; try { text = ReadFileShared(path); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (string pattern in patterns)
            {
                var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    string name = matches[i].Groups["name"].Value.Trim().Trim('"', '\'').Trim('[', ']').TrimEnd('!', '.', ',', ';');
                    if (name.Contains(':')) name = name.Substring(name.IndexOf(':') + 1).Trim();
                    if (name.Equals("is not set", StringComparison.OrdinalIgnoreCase) || name.Contains("not set", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Plausible(name) && !LooksLikeEpicIdentifier(name)) return name;
                }
            }
        }
        return "";
    }
    private static bool LooksLikeEpicIdentifier(string value) => value.Length >= 32 && value.All(c => Uri.IsHexDigit(c) || c == '-');
    private static string ReadFileShared(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    private static DateTime SafeLastWriteTimeUtc(string path) { try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }
    private static bool Plausible(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 64 && !value.Contains("token", StringComparison.OrdinalIgnoreCase) && !value.Contains("puuid", StringComparison.OrdinalIgnoreCase) && !value.Contains("unknown", StringComparison.OrdinalIgnoreCase) && !value.Contains("not set", StringComparison.OrdinalIgnoreCase) && !value.Contains("not_set", StringComparison.OrdinalIgnoreCase) && !value.Contains("none", StringComparison.OrdinalIgnoreCase) && !value.Contains("null", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("is not", StringComparison.OrdinalIgnoreCase);
    private static string? MatchNamedValue(string text, string pattern) { var m = Regex.Match(text, pattern); return m.Success ? m.Groups[1].Value.Trim().Trim('"', '\'') : null; }

    private bool IsAlreadyAdded(string platform, string username) => AllAccounts.Any(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) && a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        string username = DetectAccountName(_currentPlatform);
        if (string.IsNullOrWhiteSpace(username) && _currentPlatform.Equals("Epic", StringComparison.OrdinalIgnoreCase))
        {
            string entered = Microsoft.VisualBasic.Interaction.InputBox("Enter your Epic Games Display Name:", "Epic Account Name", "").Trim();
            if (!string.IsNullOrWhiteSpace(entered)) username = entered;
        }
        if (string.IsNullOrWhiteSpace(username) && _currentPlatform == "Riot")
        {
            username = Microsoft.VisualBasic.Interaction.InputBox("Enter Riot ID (e.g. Name#TAG):", "Riot ID", "").Trim();
        }
        if (string.IsNullOrWhiteSpace(username)) { MessageBox.Show("Sign in to your " + _currentPlatform + " account first.", "Sign In Required", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (IsAlreadyAdded(_currentPlatform, username)) { MessageBox.Show("This account is already added.", "Already Added", MessageBoxButton.OK, MessageBoxImage.Information); return; }
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
        catch (Exception ex) { MessageBox.Show("Could not start the login process. " + ex.Message, "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error); }
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
                foreach (var p in processes) { try { p.Kill(); p.WaitForExit(3000); } catch { } finally { p.Dispose(); } }
                if (Process.GetProcessesByName(n).Length == 0) break;
            }
        }
    }
    private void KillRiotProcesses() => KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "Riot Client", "VALORANT", "LeagueClient", "LeagueClientUx");
    private void SwitchSteamAccount(string username) { try { KillProcesses("steam"); using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true); if (key != null) { key.SetValue("AutoLoginUser", username); key.SetValue("RememberPassword", 1); } Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("Could not switch accounts. " + ex.Message, "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error); } }
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
        catch (Exception ex) { MessageBox.Show("Could not switch Riot account. " + ex.Message, "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error); }
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
    private void StartRiotClient(bool loginFlow)
    {
        var candidates = new List<string>();
        string installs = @"C:\ProgramData\Riot Games\RiotClientInstalls.json";
        try
        {
            if (File.Exists(installs))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(installs));
                foreach (string keyName in new[] { "rc_default", "rc_live" })
                    if (document.RootElement.TryGetProperty(keyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    {
                        string installPath = value.GetString() ?? "";
                        candidates.Add(Path.GetFileName(installPath).Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase) ? installPath : Path.Combine(installPath, "RiotClientServices.exe"));
                    }
            }
        }
        catch (IOException) { } catch (JsonException) { } catch (UnauthorizedAccessException) { }
        candidates.AddRange(new[] { @"C:\Riot Games\Riot Client\RiotClientServices.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "Riot Client", "RiotClientServices.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games", "Riot Client", "RiotClientServices.exe") });
        string? exe = candidates.FirstOrDefault(File.Exists);
        if (exe != null) Process.Start(new ProcessStartInfo(exe, "--launch-product=riot_client --launch-patchline=live") { UseShellExecute = true });
        else MessageBox.Show("Riot Client was not found.", "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private void SwitchEpicAccount(Account a)
    {
        try
        {
            KillProcesses("EpicGamesLauncher", "EpicWebHelper", "UnrealCEFSubProcess");
            string currentName = GetEpicAccountName();
            if (!string.IsNullOrWhiteSpace(currentName) && !currentName.Equals(a.Username, StringComparison.OrdinalIgnoreCase)) SaveEpicSessionSnapshot(currentName);
            ClearEpicActiveSession();
            CopyEpicSessionSnapshot(a.Username);
            StartEpicLauncher();
        }
        catch (Exception ex) { MessageBox.Show("Could not switch Epic Games account. " + ex.Message, "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void SaveEpicSessionSnapshot(string accountName)
    {
        foreach (string source in GetEpicProfileDirectories())
        {
            if (!Directory.Exists(source)) continue;
            string destination = Path.Combine(GetSessionPath("Epic", accountName), "Profiles", GetEpicProfileFolderName(source));
            CopyDirectory(source, destination);
        }
    }
    private void ClearEpicActiveSession()
    {
        foreach (string path in GetEpicProfileDirectories())
        {
            if (!Directory.Exists(path)) continue;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Directory.Delete(path, true); break; }
                catch (IOException) { if (attempt == 4) throw; System.Threading.Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { if (attempt == 4) throw; System.Threading.Thread.Sleep(500); }
            }
        }
    }
    private void CopyEpicSessionSnapshot(string accountName)
    {
        string snapshotRoot = Path.Combine(GetSessionPath("Epic", accountName), "Profiles");
        if (!Directory.Exists(snapshotRoot)) return;
        foreach (string source in Directory.GetDirectories(snapshotRoot))
        {
            string target = GetEpicProfileDirectoryForFolder(Path.GetFileName(source));
            CopyDirectory(source, target);
        }
    }
    private IEnumerable<string> GetEpicProfileDirectories()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved");
        string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Epic", "EpicGamesLauncher", "Saved");
        return new[] { local, roaming }.Distinct(StringComparer.OrdinalIgnoreCase);
    }
    private string GetEpicProfileFolderName(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    private string GetEpicProfileDirectoryForFolder(string folder)
    {
        string? match = GetEpicProfileDirectories().FirstOrDefault(p => GetEpicProfileFolderName(p).Equals(folder, StringComparison.OrdinalIgnoreCase));
        return match ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", folder);
    }
    private void StartEpicLauncher()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe");
        if (!File.Exists(exe)) exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        else MessageBox.Show("Epic Games Launcher was not found.", "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private string GetEpicSavedPath()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved");
        string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Epic", "EpicGamesLauncher", "Saved");
        return Directory.Exists(local) || !Directory.Exists(roaming) ? local : roaming;
    }
    private string GetRiotDataPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");
    private string GetSessionPath(string platform, string username) => Path.Combine(_sessionsPath, platform, string.Concat(username.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
    private void SaveSessionSnapshot(Account account)
    {
        try
        {
            string source = account.Platform == "Riot" ? GetRiotDataPath() : account.Platform == "Epic" ? GetEpicSavedPath() : Path.Combine(GetSteamPath(), "config");
            if (!Directory.Exists(source)) return;
            string target = Path.Combine(GetSessionPath(account.Platform, account.Username), account.Platform == "Riot" ? "Data" : "Saved");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyDirectory(source, target);
        }
        catch { }
    }
    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        SaveAccountsToJson();
        MessageBox.Show("Note saved.", "Willen Switcher", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveAccountsToJson()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_jsonPath, JsonSerializer.Serialize(AllAccounts, options));
        }
        catch (Exception ex) { MessageBox.Show("Could not save accounts. " + ex.Message, "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void LoadAccountsFromJson()
    {
        try
        {
            if (!File.Exists(_jsonPath)) return;
            var accounts = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_jsonPath));
            if (accounts != null) foreach (var a in accounts) AllAccounts.Add(a);
        }
        catch { }
    }
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir)) File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        foreach (string directory in Directory.GetDirectories(sourceDir)) CopyDirectory(directory, Path.Combine(destDir, Path.GetFileName(directory)));
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
