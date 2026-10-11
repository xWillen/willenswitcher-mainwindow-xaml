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
            else
            {
                MessageBox.Show("Switching for " + acc.Platform + " is coming soon!", "Coming Soon", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void SwitchSteamAccount(string username)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("steam"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch { }
            }

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
