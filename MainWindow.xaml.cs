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
        public MainWindow() { InitializeComponent(); LoadAccountsFromJson(); }

        private static string? MatchNamedValue(string text, string pattern)
        {
            Match match = Regex.Match(text, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }
        private static bool Plausible(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length < 100;
        private static string ReadFileShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        private string FindRiotAccountName()
        {
            try
            {
                string[] roots = { GetRiotDataPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config") };
                foreach (string root in roots.Where(Directory.Exists))
                foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string text;
                    try { text = ReadFileShared(path); } catch { continue; }
                    string? gameName = MatchNamedValue(text, @""game_name""\s*:\s*""([^""]+)""") ?? MatchNamedValue(text, @"(?im)game_name:\s*""?([^""\r\n]+)");
                    string? tagLine = MatchNamedValue(text, @""tag_line""\s*:\s*""([^""]+)""") ?? MatchNamedValue(text, @"(?im)tag_line:\s*""?([^""\r\n]+)");
                    if (Plausible(gameName) && Plausible(tagLine)) return gameName!.Trim() + "#" + tagLine!.Trim();
                    string? display = MatchNamedValue(text, @"(?im)displayName:\s*""?([^""\r\n]+)") ?? MatchNamedValue(text, @""game_name""\s*:\s*""([^""]+)""");
                    if (Plausible(display)) return display!.Trim();
                    if (path.Contains(Path.DirectorySeparatorChar + "Logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        string? logged = MatchNamedValue(text, @"(?im)Logged in as\s+([^\r\n,]+)") ?? MatchNamedValue(text, @"(?im)Riot ID:\s*([^\r\n,]+)") ?? MatchNamedValue(text, @"(?im)Player\s+([^\r\n#]+#[A-Za-z0-9]+)");
                        if (Plausible(logged)) return logged!.Trim();
                    }
                }
            }
            catch { }
            return "";
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
