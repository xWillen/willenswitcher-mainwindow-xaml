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

    public MainWindow()
    {
        InitializeComponent();
        AccountsList.ItemsSource = FilteredAccounts;
        LoadAccountsFromJson();
        RefreshFilteredList();
    }

    private void PlatformNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string platform)
        {
            _currentPlatform = platform;
            PlatformTitleText.Text = platform + " Accounts";
            RefreshFilteredList();
        }
    }

    private void RefreshFilteredList()
    {
        FilteredAccounts.Clear();
        foreach (var acc in AllAccounts.Where(a => a.Platform == _currentPlatform))
        {
            FilteredAccounts.Add(acc);
        }
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        string username = "";

        if (_currentPlatform == "Steam")
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                username = key?.GetValue("AutoLoginUser")?.ToString() ?? "";
            }
            catch { }
        }
        else if (_currentPlatform == "Riot")
        {
            username = GetRiotAccountName();
        }
        else if (_currentPlatform == "Epic")
        {
            username = GetEpicAccountName();
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            username = "New_" + _currentPlatform + "_Account";
        }

        if (AllAccounts.Any(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && a.Platform == _currentPlatform))
        {
            MessageBox.Show("This account is already added!", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AllAccounts.Add(new Account
        {
            Username = username,
            Platform = _currentPlatform,
            Note = "Main Account"
        });

        SaveAccountsToJson();
        RefreshFilteredList();
    }

    private string GetRiotAccountName()
    {
        try
        {
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Riot Games", "Riot Client", "Data", "RiotClientPrivateSettings.yaml");
            if (File.Exists(settingsPath))
            {
                string content = File.ReadAllText(settingsPath);
                foreach (string line in content.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("username:", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed.Substring(trimmed.IndexOf(':') + 1).Trim().Trim('"', '\'');
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
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini");
            if (File.Exists(configPath))
            {
                foreach (string line in File.ReadAllLines(configPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("LastLoginUserName=", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed.Substring("LastLoginUserName=".Length).Trim().Trim('"');
                    }
                }
            }
        }
        catch { }
        return "";
    }

    private void LoginNewAccount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlatform == "Steam")
            {
                KillProcesses("steam");
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                {
                    key?.SetValue("AutoLoginUser", "");
                }
                Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                MessageBox.Show(
                    "Please log in to your Steam account. Once logged in, click 'Add Account' to save it automatically.",
                    "Login New Account", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (_currentPlatform == "Riot")
            {
                KillProcesses("RiotClientServices");
                StartRiotClient();
                MessageBox.Show("Please log in to Riot Client, then click Add Account.",
                    "Login New Account", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (_currentPlatform == "Epic")
            {
                KillProcesses("EpicGamesLauncher");
                StartEpicLauncher();
                MessageBox.Show("Please log in to Epic Games, then click Add Account.",
                    "Login New Account", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the login flow: " + ex.Message,
                "Login Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (acc.Platform == "Steam")
            {
                SwitchSteamAccount(acc.Username);
            }
            else if (acc.Platform == "Riot")
            {
                SwitchRiotAccount();
            }
            else if (acc.Platform == "Epic")
            {
                SwitchEpicAccount();
            }
        }
    }

    private void KillProcesses(params string[] processNames)
    {
        foreach (string processName in processNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
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

    private void SwitchRiotAccount()
    {
        try
        {
            KillProcesses("RiotClientServices", "VALORANT", "LeagueClient", "LeagueClientUx", "RiotClientUx", "RiotClientUxRender");
            StartRiotClient();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not restart Riot Client: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartRiotClient()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games", "Riot Client", "RiotClientServices.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games", "Riot Client", "RiotClientServices.exe")
        };
        string executable = candidates.FirstOrDefault(File.Exists);
        if (executable != null)
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo("riotclient://") { UseShellExecute = true });
        }
    }

    private void SwitchEpicAccount()
    {
        try
        {
            KillProcesses("EpicGamesLauncher");
            StartEpicLauncher();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not restart Epic Games Launcher: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartEpicLauncher()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe")
        };
        string executable = candidates.FirstOrDefault(File.Exists);
        if (executable != null)
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo("com.epicgames.launcher://") { UseShellExecute = true });
        }
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
            if (File.Exists(_jsonPath))
            {
                var json = File.ReadAllText(_jsonPath);
                var list = JsonSerializer.Deserialize<List<Account>>(json);
                if (list != null)
                {
                    AllAccounts.Clear();
                    foreach (var item in list)
                    {
                        AllAccounts.Add(item);
                    }
                }
            }
        }
        catch { }
    }

    private void SaveAccountsToJson()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(AllAccounts.ToList(), options);
            File.WriteAllText(_jsonPath, json);
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
