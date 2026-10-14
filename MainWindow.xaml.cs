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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Threading.Tasks;

namespace WillenSwitcher;

public partial class MainWindow : Window
{
    public ObservableCollection<Account> AllAccounts { get; set; } = new();
    public ObservableCollection<Account> FilteredAccounts { get; set; } = new();
    private string _currentPlatform = "Steam";
    private readonly string _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accounts.json");
    private readonly string _sessionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedSessions");
    private TaskCompletionSource<string?>? _dialogTcs;

    private Task<string?> ShowCustomInputDialogAsync(string title, string message, string defaultText = "")
    {
        _dialogTcs = new TaskCompletionSource<string?>();
        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        DialogInputBox.Text = defaultText;
        DialogInputBox.Visibility = Visibility.Visible;
        DialogCancelButton.Visibility = Visibility.Visible;
        CustomDialogOverlay.Visibility = Visibility.Visible;
        DialogInputBox.Focus();
        DialogInputBox.SelectAll();
        return _dialogTcs.Task;
    }

    private void DialogOkButton_Click(object sender, RoutedEventArgs e)
    {
        CustomDialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(DialogInputBox.Visibility == Visibility.Visible ? DialogInputBox.Text : string.Empty);
    }

    private void DialogCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CustomDialogOverlay.Visibility = Visibility.Collapsed;
        _dialogTcs?.TrySetResult(null);
    }

    private Task ShowCustomMessageAsync(string title, string message)
    {
        _dialogTcs = new TaskCompletionSource<string?>();
        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        DialogInputBox.Visibility = Visibility.Collapsed;
        DialogCancelButton.Visibility = Visibility.Collapsed;
        CustomDialogOverlay.Visibility = Visibility.Visible;
        return _dialogTcs.Task;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not Button)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public MainWindow()
    {
        InitializeComponent(); AccountsList.ItemsSource = FilteredAccounts; LoadAccountsFromJson();
        foreach (var account in AllAccounts.Where(a => a.Platform.Equals("Steam", StringComparison.OrdinalIgnoreCase))) UpdateSteamAccountDetails(account);
        foreach (var account in AllAccounts) ApplyPlatformAvatar(account);
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
        ApplyPlatformAvatar(account);
        if (platform.Equals("Steam", StringComparison.OrdinalIgnoreCase)) UpdateSteamAccountDetails(account);
        AllAccounts.Add(account); SaveSessionSnapshot(account); SaveAccountsToJson();
    }
    private void ApplyPlatformAvatar(Account account)
    {
        if (account.Platform.Equals("Riot", StringComparison.OrdinalIgnoreCase)) account.AvatarPath = "/Assets/riot.png";
    }
    private string DetectAccountName(string platform) => platform switch { "Steam" => GetSteamAccountName(), "Riot" => GetRiotAccountName(), _ => "" };

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
                string? gameName = MatchNamedValue(text, "\\\"\\\"\\\"game_name\\\"\\\"\\s*:\\s*\\\"\\\"([^\\\"]+)\\\"") ?? MatchNamedValue(text, "(?im)game_name:\\s*\\\"?([^\\\"\\r\\n]+)");
                string? tagLine = MatchNamedValue(text, "\\\"\\\"\\\"tag_line\\\"\\\"\\s*:\\s*\\\"\\\"([^\\\"]+)\\\"") ?? MatchNamedValue(text, "(?im)tag_line:\\s*\\\"?([^\\\"\\r\\n]+)");
                if (Plausible(gameName) && Plausible(tagLine)) return gameName!.Trim() + "#" + tagLine!.Trim();
                string? display = MatchNamedValue(text, "(?im)displayName:\\s*\\\"?([^\\\"\\r\\n]+)") ?? MatchNamedValue(text, "\\\"\\\"\\\"game_name\\\"\\\"\\s*:\\s*\\\"\\\"([^\\\"]+)\\\"");
                if (Plausible(display)) return display!.Trim();
                if (path.Contains(Path.DirectorySeparatorChar + "Logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    string? logged = MatchNamedValue(text, "(?im)display name update received:\\s*([^\\r\\n,]+)") ?? MatchNamedValue(text, "(?im)Logged in as\\s+([^\\r\\n,]+)") ?? MatchNamedValue(text, "(?im)Riot ID:\\s*([^\\r\\n,]+)") ?? MatchNamedValue(text, "(?im)Player\\s+([^\\r\\n#]+#[A-Za-z0-9]+)");
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

    private static string ReadFileShared(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    private static DateTime SafeLastWriteTimeUtc(string path) { try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }
    private static bool Plausible(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 64 && !value.Contains("token", StringComparison.OrdinalIgnoreCase) && !value.Contains("puuid", StringComparison.OrdinalIgnoreCase) && !value.Contains("unknown", StringComparison.OrdinalIgnoreCase) && !value.Contains("not set", StringComparison.OrdinalIgnoreCase) && !value.Contains("not_set", StringComparison.OrdinalIgnoreCase) && !value.Contains("none", StringComparison.OrdinalIgnoreCase) && !value.Contains("null", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("is not", StringComparison.OrdinalIgnoreCase);
    private static string? MatchNamedValue(string text, string pattern) { var m = Regex.Match(text, pattern); return m.Success ? m.Groups[1].Value.Trim().Trim('"', '\'') : null; }

    private bool IsAlreadyAdded(string platform, string username) => AllAccounts.Any(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) && a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        string username = DetectAccountName(_currentPlatform);
        if (string.IsNullOrWhiteSpace(username) && _currentPlatform == "Riot")
        {
            string? entered = await ShowCustomInputDialogAsync("Riot ID", "Enter Riot ID (e.g. Name#TAG):");
            if (entered == null) return;
            username = entered.Trim();
        }
        if (string.IsNullOrWhiteSpace(username)) { await ShowCustomMessageAsync("Sign In Required", "Sign in to your " + _currentPlatform + " account first."); return; }
        if (IsAlreadyAdded(_currentPlatform, username)) { await ShowCustomMessageAsync("Already Added", "This account is already added."); return; }
        var account = new Account { Username = username, DisplayName = username, Platform = _currentPlatform, Note = "Main Account" };
        ApplyPlatformAvatar(account);
        if (_currentPlatform == "Steam") UpdateSteamAccountDetails(account);
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
        }
        catch (Exception ex) { MessageBox.Show("Could not start the login process. " + ex.Message, "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void DeleteAccount_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement el && el.DataContext is Account a) { AllAccounts.Remove(a); SaveAccountsToJson(); RefreshFilteredList(); } }
    private void SwitchAccount_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement el && el.DataContext is Account a) { if (a.Platform == "Steam") SwitchSteamAccount(a.Username); else if (a.Platform == "Riot") SwitchRiotAccount(a); } }
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
    private string GetRiotDataPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");
    private string GetSessionPath(string platform, string accountName) => Path.Combine(_sessionsPath, platform, SafeFolderName(accountName));
    private string SafeFolderName(string value) { foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return value; }
    private void SaveSessionSnapshot(Account account)
    {
        if (account.Platform == "Riot") SaveRiotSessionSnapshot(account.Username);
    }
    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string target = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, target, true);
        }
        foreach (string directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }
    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is Account account)
        {
            SaveAccountsToJson();
            MessageBox.Show("Note saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private void SaveAccountsToJson()
    {
        try
        {
            string json = JsonSerializer.Serialize(AllAccounts, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_jsonPath, json);
        }
        catch (Exception ex) { MessageBox.Show("Could not save accounts: " + ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void LoadAccountsFromJson()
    {
        try
        {
            if (!File.Exists(_jsonPath)) return;
            string json = File.ReadAllText(_jsonPath);
            var accounts = JsonSerializer.Deserialize<ObservableCollection<Account>>(json);
            if (accounts != null) foreach (var account in accounts) AllAccounts.Add(account);
        }
        catch (Exception ex) { MessageBox.Show("Could not load accounts: " + ex.Message, "Load Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}

public class Account
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Note { get; set; } = "";
    public string AvatarPath { get; set; } = "";
    [JsonIgnore]
    public ImageSource? AvatarSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AvatarPath)) return null;
            if (Uri.TryCreate(AvatarPath, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return new BitmapImage(uri);
            return File.Exists(AvatarPath) ? new BitmapImage(new Uri(AvatarPath, UriKind.Absolute)) : null;
        }
    }
}
