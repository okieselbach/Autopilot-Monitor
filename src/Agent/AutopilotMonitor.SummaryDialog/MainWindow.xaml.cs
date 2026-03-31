using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutopilotMonitor.SummaryDialog.Models;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AutopilotMonitor.SummaryDialog
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _countdownTimer;
        private int _remainingSeconds;
        private FinalStatus _status;

        // Theme colors
        private bool _isDarkTheme;

        public MainWindow()
        {
            App.Log("MainWindow constructor — InitializeComponent");
            InitializeComponent();
            App.Log("MainWindow constructor — done");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            App.Log("Window_Loaded — start");
            try
            {
                _isDarkTheme = App.ForceTheme ?? DetectDarkTheme();
                App.Log($"Theme: isDark={_isDarkTheme} (forced={App.ForceTheme})");
                if (_isDarkTheme)
                    ApplyDarkTheme();

                ApplyScrollBarStyle();
                ApplyContentClip();
                ApplyRoundedCorners();

                LoadStatusData();

                var isSuccess = _status == null ||
                    string.Equals(_status.Outcome, "completed", StringComparison.OrdinalIgnoreCase);
                ApplyTaskbarIcon(isSuccess);

                RenderOutcome();
                RenderAppList();
                StartCountdown();

                // Load branding image (async, best-effort)
                if (!string.IsNullOrEmpty(App.BrandingImageUrl))
                {
                    await LoadBrandingImageAsync();
                }

                App.Log("Window_Loaded — complete, window should be visible");
            }
            catch (Exception ex)
            {
                App.LogError($"Window_Loaded FAILED: {ex}");
            }
        }

        private void LoadStatusData()
        {
            try
            {
                if (!string.IsNullOrEmpty(App.StatusFilePath) && File.Exists(App.StatusFilePath))
                {
                    var json = File.ReadAllText(App.StatusFilePath);
                    _status = JsonConvert.DeserializeObject<FinalStatus>(json);
                    App.Log($"LoadStatusData: outcome={_status?.Outcome} apps={_status?.AppSummary?.TotalApps}");
                }
                else
                {
                    App.Log($"LoadStatusData: status file missing or empty (path={App.StatusFilePath})");
                }
            }
            catch (Exception ex)
            {
                App.LogError($"LoadStatusData FAILED: {ex.Message}");
                _status = null;
            }
        }

        private void RenderOutcome()
        {
            if (_status == null)
            {
                OutcomeText.Text = "Enrollment Completed";
                DurationText.Text = "Details unavailable";
                SuccessIcon.Visibility = Visibility.Visible;
                return;
            }

            var isSuccess = string.Equals(_status.Outcome, "completed", StringComparison.OrdinalIgnoreCase);

            if (isSuccess)
            {
                OutcomeText.Text = "Enrollment Completed Successfully";
                SuccessIcon.Visibility = Visibility.Visible;
                FailureIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                OutcomeText.Text = "Enrollment Failed";
                SuccessIcon.Visibility = Visibility.Collapsed;
                FailureIcon.Visibility = Visibility.Visible;

                OutcomeText.Foreground = (Brush)FindResource("ErrorColor");
            }

            // Format duration
            var duration = TimeSpan.FromSeconds(_status.AgentUptimeSeconds);
            if (duration.TotalHours >= 1)
                DurationText.Text = $"Duration: {(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
            else if (duration.TotalMinutes >= 1)
                DurationText.Text = $"Duration: {(int)duration.TotalMinutes} min {duration.Seconds} sec";
            else
                DurationText.Text = $"Duration: {duration.Seconds} sec";
        }

        private void RenderAppList()
        {
            if (_status?.AppSummary == null || _status.AppSummary.TotalApps == 0)
            {
                NoAppsText.Visibility = Visibility.Visible;
                AppSummaryText.Visibility = Visibility.Collapsed;
                return;
            }

            var summary = _status.AppSummary;

            // Count skipped apps across all phases to exclude from totals
            var skippedCount = _status.PackageStatesByPhase != null
                ? _status.PackageStatesByPhase.Values.SelectMany(p => p)
                    .Count(a => string.Equals(a.State, "Skipped", StringComparison.OrdinalIgnoreCase))
                : 0;

            var visibleTotal = summary.TotalApps - skippedCount;
            var visibleCompleted = summary.CompletedApps - skippedCount;
            if (visibleTotal < 0) visibleTotal = 0;
            if (visibleCompleted < 0) visibleCompleted = 0;

            // If all apps were skipped, show "no apps" message
            if (visibleTotal == 0)
            {
                NoAppsText.Visibility = Visibility.Visible;
                AppSummaryText.Visibility = Visibility.Collapsed;
                return;
            }

            // Update progress bar
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var parentGrid = (Grid)ProgressBarSuccess.Parent;
                var totalWidth = parentGrid.ActualWidth;
                if (totalWidth > 0 && visibleTotal > 0)
                {
                    ProgressBarSuccess.Width = (double)visibleCompleted / visibleTotal * totalWidth;
                    if (summary.ErrorCount > 0)
                        ProgressBarSuccess.Background = (Brush)FindResource("ProgressError");
                }
            }));

            // Summary text
            if (summary.ErrorCount > 0)
            {
                AppSummaryText.Text = $"{visibleCompleted} of {visibleTotal} apps installed, {summary.ErrorCount} failed";
                AppSummaryText.Foreground = (Brush)FindResource("ErrorColor");
            }
            else
            {
                AppSummaryText.Text = $"{visibleCompleted} of {visibleTotal} apps installed";
            }

            // Render phases
            if (_status.PackageStatesByPhase != null)
            {
                foreach (var phase in _status.PackageStatesByPhase)
                {
                    AddPhaseSection(phase.Key, phase.Value);
                }
            }
        }

        private void AddPhaseSection(string phaseName, List<PackageInfo> apps)
        {
            if (apps == null || apps.Count == 0) return;

            // Filter out skipped apps — they are not actionable for the user
            var visibleApps = apps.Where(a => !string.Equals(a.State, "Skipped", StringComparison.OrdinalIgnoreCase)).ToList();
            if (visibleApps.Count == 0) return;

            // Phase header
            var header = new TextBlock
            {
                Text = $"{FormatPhaseName(phaseName)} ({visibleApps.Count})",
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 12,
                Foreground = (Brush)FindResource("PhaseHeaderText"),
                Margin = new Thickness(0, 8, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            AppListPanel.Children.Add(header);

            foreach (var app in visibleApps)
            {
                AppListPanel.Children.Add(CreateAppItem(app));
            }
        }

        private Border CreateAppItem(PackageInfo app)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Status icon
            var iconText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            if (app.IsError)
            {
                iconText.Text = "\uE711"; // X
                iconText.Foreground = (Brush)FindResource("ErrorColor");
            }
            else if (app.IsCompleted)
            {
                iconText.Text = "\uE73E"; // Checkmark
                iconText.Foreground = (Brush)FindResource("SuccessColor");
            }
            else
            {
                iconText.Text = "\uE738"; // Dash/circle
                iconText.Foreground = (Brush)FindResource("SecondaryText");
            }

            Grid.SetColumn(iconText, 0);
            grid.Children.Add(iconText);

            // App name
            var nameText = new TextBlock
            {
                Text = app.AppName ?? "Unknown App",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = (Brush)FindResource("PrimaryText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 8, 0)
            };
            Grid.SetColumn(nameText, 1);
            grid.Children.Add(nameText);

            // State label
            if (app.IsError)
            {
                var stateText = new TextBlock
                {
                    Text = "Error",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("ErrorColor"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(stateText, 2);
                grid.Children.Add(stateText);
            }

            var border = new Border
            {
                Child = grid,
                Padding = new Thickness(6, 6, 8, 6),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 1, 0, 1),
                Background = Brushes.Transparent
            };

            // Hover effect
            border.MouseEnter += (s, e) => border.Background = (Brush)FindResource("AppItemHover");
            border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;

            return border;
        }

        private async System.Threading.Tasks.Task LoadBrandingImageAsync()
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var bytes = await client.GetByteArrayAsync(App.BrandingImageUrl);
                    var bitmap = new BitmapImage();
                    using (var ms = new System.IO.MemoryStream(bytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }

                    BrandingImage.Source = bitmap;
                    BrandingBanner.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                App.Log($"LoadBrandingImage: skipped ({ex.Message})");
                BrandingBanner.Visibility = Visibility.Collapsed;
            }
        }

        private void StartCountdown()
        {
            _remainingSeconds = App.TimeoutSeconds;
            if (_remainingSeconds <= 0)
            {
                CountdownText.Visibility = Visibility.Collapsed;
                return;
            }

            CountdownText.Text = $"Auto-closing in {_remainingSeconds}s";

            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += (s, e) =>
            {
                _remainingSeconds--;
                if (_remainingSeconds <= 0)
                {
                    _countdownTimer.Stop();
                    Close();
                }
                else
                {
                    CountdownText.Text = $"Auto-closing in {_remainingSeconds}s";
                }
            };
            _countdownTimer.Start();
        }

        private void ApplyDarkTheme()
        {
            Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
            Resources["TitleBarBackground"] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            Resources["BorderColor"] = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            Resources["PrimaryText"] = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB));
            Resources["SecondaryText"] = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            Resources["SuccessColor"] = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            Resources["SuccessBackground"] = new SolidColorBrush(Color.FromRgb(0x06, 0x4E, 0x3B));
            Resources["ErrorColor"] = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            Resources["ErrorBackground"] = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D));
            Resources["ProgressBackground"] = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            Resources["ProgressSuccess"] = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            Resources["ProgressError"] = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            Resources["SectionBackground"] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            Resources["ButtonBackground"] = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            Resources["ButtonHover"] = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
            Resources["ButtonText"] = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB));
            Resources["CloseButtonHover"] = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D));
            Resources["PhaseHeaderText"] = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
            Resources["AppItemHover"] = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
        }

        /// <summary>
        /// Applies a slim, modern scrollbar style matching the current theme.
        /// Completely re-templates the vertical ScrollBar to remove arrow buttons (Win11 style).
        /// </summary>
        private void ApplyScrollBarStyle()
        {
            var trackColor = _isDarkTheme ? "#FF1F2937" : "#FFF9FAFB";
            var thumbColor = _isDarkTheme ? "#FF4B5563" : "#FFD1D5DB";
            var thumbHoverColor = _isDarkTheme ? "#FF6B7280" : "#FF9CA3AF";

            // Build the ScrollBar style via XAML string — this is the most reliable way to
            // re-template Track (which uses CLR properties, not DPs, for its children).
            var xaml = $@"
<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
       TargetType=""{{x:Type ScrollBar}}"">
    <Setter Property=""Width"" Value=""8""/>
    <Setter Property=""MinWidth"" Value=""8""/>
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""BorderThickness"" Value=""0""/>
    <Setter Property=""Margin"" Value=""0""/>
    <Setter Property=""Padding"" Value=""0""/>
    <Setter Property=""Template"">
        <Setter.Value>
            <ControlTemplate TargetType=""{{x:Type ScrollBar}}"">
                <Border Background=""{trackColor}"" CornerRadius=""4"">
                    <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
                        <Track.DecreaseRepeatButton>
                            <RepeatButton Command=""ScrollBar.PageUpCommand"" Focusable=""False"">
                                <RepeatButton.Template>
                                    <ControlTemplate TargetType=""RepeatButton"">
                                        <Border Background=""Transparent""/>
                                    </ControlTemplate>
                                </RepeatButton.Template>
                            </RepeatButton>
                        </Track.DecreaseRepeatButton>
                        <Track.Thumb>
                            <Thumb MinHeight=""20"">
                                <Thumb.Template>
                                    <ControlTemplate TargetType=""Thumb"">
                                        <Border x:Name=""ThumbBorder"" Background=""{thumbColor}""
                                                CornerRadius=""3"" Margin=""1,2,1,2""/>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                                <Setter TargetName=""ThumbBorder"" Property=""Background"" Value=""{thumbHoverColor}""/>
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Thumb.Template>
                            </Thumb>
                        </Track.Thumb>
                        <Track.IncreaseRepeatButton>
                            <RepeatButton Command=""ScrollBar.PageDownCommand"" Focusable=""False"">
                                <RepeatButton.Template>
                                    <ControlTemplate TargetType=""RepeatButton"">
                                        <Border Background=""Transparent""/>
                                    </ControlTemplate>
                                </RepeatButton.Template>
                            </RepeatButton>
                        </Track.IncreaseRepeatButton>
                    </Track>
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";

            var style = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
        }

        /// <summary>
        /// Tells Windows 11 DWM to round the window corners natively at the compositor level.
        /// Without AllowsTransparency the Win32 window is rectangular — this API makes the
        /// OS clip it to rounded corners, matching our Border's CornerRadius.
        /// Silently ignored on Windows 10 (no visual effect, no error).
        /// </summary>
        private void ApplyRoundedCorners()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
                int preference = 2;
                DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            }
            catch (Exception ex)
            {
                App.Log($"ApplyRoundedCorners: {ex.Message}");
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Clips the inner content Grid to a rounded rectangle so child elements
        /// (like the title bar) don't bleed past the outer Border's rounded corners.
        /// </summary>
        private void ApplyContentClip()
        {
            // Clip the entire content Border to a rounded rectangle.
            // This prevents child backgrounds from bleeding past the rounded corners.
            const double radius = 12; // matches Border CornerRadius

            void UpdateClip()
            {
                if (ContentBorder.ActualWidth > 0 && ContentBorder.ActualHeight > 0)
                {
                    ContentBorder.Clip = new RectangleGeometry(
                        new Rect(0, 0, ContentBorder.ActualWidth, ContentBorder.ActualHeight),
                        radius, radius);
                }
            }

            ContentBorder.SizeChanged += (s, args) => UpdateClip();
            UpdateClip(); // Set immediately — layout is already complete at Window_Loaded time
        }

        /// <summary>
        /// Generates a green checkmark icon and sets it as the window/taskbar icon.
        /// </summary>
        /// <summary>
        /// Sets the taskbar icon. Success icon is loaded statically via XAML;
        /// this method only overrides it with the error icon when enrollment failed.
        /// </summary>
        private void ApplyTaskbarIcon(bool isSuccess)
        {
            if (!isSuccess)
            {
                try
                {
                    var uri = new Uri("pack://application:,,,/icon-error.ico", UriKind.Absolute);
                    Icon = new BitmapImage(uri);
                }
                catch (Exception ex)
                {
                    App.Log($"ApplyTaskbarIcon: failed to load error icon ({ex.Message}), keeping success icon");
                }
            }
        }

        private static bool DetectDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key?.GetValue("AppsUseLightTheme");
                    var isDark = value is int i && i == 0;
                    App.Log($"DetectDarkTheme: AppsUseLightTheme={value} → isDark={isDark}");
                    return isDark;
                }
            }
            catch (Exception ex)
            {
                App.Log($"DetectDarkTheme: registry read failed ({ex.Message}), defaulting to light");
                return false;
            }
        }

        private static string FormatPhaseName(string phase)
        {
            // Convert "DeviceSetup" -> "Device Setup", "AccountSetup" -> "Account Setup"
            if (string.IsNullOrEmpty(phase)) return phase;

            var result = new System.Text.StringBuilder();
            for (int i = 0; i < phase.Length; i++)
            {
                if (i > 0 && char.IsUpper(phase[i]) && !char.IsUpper(phase[i - 1]))
                    result.Append(' ');
                result.Append(phase[i]);
            }
            return result.ToString();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _countdownTimer?.Stop();
            Close();
        }
    }

    // Extension helper for Parent casting
    internal static class FrameworkElementExtensions
    {
        internal static T As<T>(this DependencyObject obj) where T : class
        {
            return obj as T;
        }
    }
}
