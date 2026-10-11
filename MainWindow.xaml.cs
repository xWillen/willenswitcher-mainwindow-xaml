using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        DetectAndSaveActiveAccount("Steam");
        RefreshFilteredList();
    }

    private void PlatformNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string platform)
        {
            _currentPlatform = platform;
            PlatformTitleText.Text = platform + " Accounts";
            DetectAndSaveActiveAccount(platform);
            RefreshFilteredList();
        }
    }

    private void RefreshFilteredList()
    {
        FilteredAccounts.Clear();
        foreach (var acc in AllAccounts.Where(a => a.Platform == _currentPlatform))
            FilteredAccounts.Add(acc);
    }

    private void DetectAndSaveActiveAccount(string platform)
    {
        string username = DetectAccountName(platform);
        if (string.IsNullOrWhiteSpace(username) || IsAlreadyAdded(platform, username))
            return;

        var account = new Account { Username = username, Platform = platform, Note = "Main Account" };
        AllAccounts.Add(account);
        SaveSessionSnapshot(account);
        SaveAccountsToJson();
        if (platform == _currentPlatform)
            RefreshFilteredList();
    }

    private string DetectAccountName(string platform) => platform switch
    {
        "Steam" => GetSteamAccountName(),
        "Riot" => GetRiotAccountName(),
        "Epic" => GetEpicAccountName(),
        _ => ""
    };

    private string GetSteamAccountName()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("AutoLoginUser")?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private string GetRiotAccountName()
    {
        try
        {
            string dataPath = GetRiotDataPath();
            foreach (string file in new[] { "RiotGamesPrivateSettings.yaml", "RiotClientPrivateSettings.yaml" })
            {
                string settingsPath = Path.Combine(dataPath, file);
                if (!File.Exists(settingsPath)) continue;
                string content = File.ReadAllText(settingsPath);
                foreach (string line in content.Split('\n'))
                {
                    string trimmed = line.Trim();
                    foreach (string key in new[] { "username:", "name:", "sub:", "subject:", "puuid:" })
                    {
                        if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        {
                            string value = trimmed[(trimmed.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
                            if (!string.IsNullOrWhiteSpace(value)) return value;
                        }
                    }
                }
            }
        }
        catch { }
        return "";
    }

    private string GetEpicAccountName()
    {
        try
        {
            string configPath = Path.Combine(GetEpicSavedPath(), "Config", "Windows", "GameUserSettings.ini");
            if (!File.Exists(configPath)) return "";
            foreach (string line in File.ReadAllLines(configPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("LastLoginUserName=", StringComparison.OrdinalIgnoreCase))
                    return trimmed[("LastLoginUserName=".Length)..].Trim().Trim('"');
            }
        }
        catch { }
        return "";
    }

    private bool IsAlreadyAdded(string platform, string username) =>
        AllAccounts.Any(a => a.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase) &&
                             a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        string username = DetectAccountName(_currentPlatform);
        if (string.IsNullOrWhiteSpace(username))
            username = "New_" + _currentPlatform + "_Account";

        if (IsAlreadyAdded(_currentPlatform, username))
        {
            MessageBox.Show("This account is already added!", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var account = new Account { Username = username, Platform = _currentPlatform, Note = "Main Account" };
        AllAccounts.Add(account);
        SaveSessionSnapshot(account);
        SaveAccountsToJson();
        RefreshFilteredList();
    }

    private void LoginNewAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlatform == "Steam")
            {
                KillProcesses("steam");
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                    key?.SetValue("AutoLoginUser", "");
                Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                MessageBox.Show("Please log in to your Steam account. Once logged in, click 'Add Account' to save it automatically.", "Login New Account", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (_currentPlatform == "Riot")
            {
                KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "VALORANT", "LeagueClient", "LeagueClientUx");
                string activeSettings = FindRiotSettingsFile();
                if (activeSettings != null)
                    File.Move(activeSettings, activeSettings + ".willenswitcher-backup", true);
                StartRiotClient();
                MessageBox.Show("Please log in to Riot Client, then click Add Account.", "Login New Account", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (_currentPlatform == "Epic")
            {
                KillProcesses("EpicGamesLauncher");
                StartEpicLauncher();
                MessageBox.Show("Please log in to Epic Games, then click Add Account.", "Login New Account", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the login flow: " + ex.Message, "Login Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Account acc)
        {
            AllAccounts.Remove(acc);
            SaveAccountsToJson();
            RefreshFilteredList();
        }
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
        foreach (string processName in processNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try { process.Kill(); process.WaitForExit(3000); }
                catch { }
                finally { process.Dispose(); }
            }
        }
    }

    private void SwitchSteamAccount(string username)
    {
        try
        {
            KillProcesses("steam");
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
            {
                if (key != null)
                {
                    key.SetValue("AutoLoginUser", username);
                    key.SetValue("RememberPassword", 1);
                }
            }
            Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Error switching account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SwitchRiotAccount(Account account)
    {
        try
        {
            SaveSessionSnapshot(account);
            KillProcesses("RiotClientServices", "RiotClientUx", "RiotClientUxRender", "VALORANT", "LeagueClient", "LeagueClientUx");
            string backupPath = GetSessionPath("Riot", account.Username);
            string dataPath = GetRiotDataPath();
            if (Directory.Exists(Path.Combine(backupPath, "Data")))
                CopyDirectory(Path.Combine(backupPath, "Data"), dataPath);
            StartRiotClient();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not switch Riot account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SwitchEpicAccount(Account account)
    {
        try
        {
            SaveSessionSnapshot(account);
            KillProcesses("EpicGamesLauncher");
            string backupPath = GetSessionPath("Epic", account.Username);
            string savedPath = GetEpicSavedPath();
            if (Directory.Exists(Path.Combine(backupPath, "Saved")))
                CopyDirectory(Path.Combine(backupPath, "Saved"), savedPath);
            StartEpicLauncher();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not switch Epic Games account: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveSessionSnapshot(Account account)
    {
        try
        {
            string platformPath = GetSessionPath(account.Platform, account.Username);
            if (account.Platform == "Riot")
            {
                string dataPath = GetRiotDataPath();
                if (Directory.Exists(dataPath)) CopyDirectory(dataPath, Path.Combine(platformPath, "Data"));
            }
            else if (account.Platform == "Epic")
            {
                string savedPath = GetEpicSavedPath();
                if (Directory.Exists(savedPath)) CopyDirectory(savedPath, Path.Combine(platformPath, "Saved"));
            }
        }
        catch { }
    }

    private string GetSessionPath(string platform, string username) =>
        Path.Combine(_sessionsPath, platform, MakeSafeFileName(username));

    private static string MakeSafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "UnknownAccount" : value;
    }

    private string GetRiotDataPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data");

    private string GetEpicSavedPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved");

    private string FindRiotSettingsFile()
    {
        string dataPath = GetRiotDataPath();
        foreach (string name in new[] { "RiotGamesPrivateSettings.yaml", "RiotClientPrivateSettings.yaml" })
        {
            string candidate = Path.Combine(dataPath, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private void StartRiotClient()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "Riot Client", "RiotClientServices.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games", "Riot Client", "RiotClientServices.exe"),
            @"C:\Riot Games\Riot Client\RiotClientServices.exe"
        };
        string executable = candidates.FirstOrDefault(File.Exists);
        Process.Start(new ProcessStartInfo(executable ?? "riotclient://") { UseShellExecute = true });
    }

    private void StartEpicLauncher()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe")
        };
        string executable = candidates.FirstOrDefault(File.Exists);
        Process.Start(new ProcessStartInfo(executable ?? "com.epicgames.launcher://") { UseShellExecute = true });
    }

    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        SaveAccountsToJson();
        MessageBox.Show("Note saved successfully!", "Willen Switcher", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LoadAccountsFromJson()
    {
        try
        {
            if (!File.Exists(_jsonPath)) return;
            var list = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_jsonPath));
            if (list == null) return;
            AllAccounts.Clear();
            foreach (var item in list) AllAccounts.Add(item);
        }
        catch { }
    }

    private void SaveAccountsToJson()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_jsonPath, JsonSerializer.Serialize(AllAccounts.ToList(), options));
        }
        catch { }
    }
}

public class Account
{
    public string Username { get; set; } = "";
    public string Platform { get; set; } = "Steam";
    public string Note { get; set; } = "";
}
