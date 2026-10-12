using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WillenSwitcher
{
    public partial class MainWindow : Window
    {
        private string _currentPlatform = "Steam";
        private readonly string _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accounts.json");
        private readonly string _sessionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedSessions");
