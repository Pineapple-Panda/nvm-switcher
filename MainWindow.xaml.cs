using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nvm03;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<NodeVersion> _versions = [];
    private readonly Dictionary<string, List<string>> _tagsByVersion = LoadTags();
    private bool _isLoading;

    public MainWindow()
    {
        InitializeComponent();
        VersionsList.ItemsSource = _versions;
        Loaded += async (_, _) =>
        {
            await RefreshVersionsAsync();
            await LoadLatestVersionAsync();
        };
    }

    private async Task RefreshVersionsAsync()
    {
        SetBusy(true, "Loading installed Node.js versions...");

        try
        {
            var result = await RunNvmAsync("list");
            if (result.ExitCode != 0)
            {
                SetStatus(result.Output, true);
                return;
            }

            var versions = ParseVersions(result.Output);
            _versions.Clear();
            foreach (var version in versions)
            {
                _versions.Add(version with { Tags = _tagsByVersion.GetValueOrDefault(version.Version, []) });
            }

            EmptyState.Visibility = _versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetStatus(_versions.Count == 0 ? "No installed Node.js versions found." : $"Loaded {_versions.Count} installed version(s).", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Unable to run nvm: {exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadLatestVersionAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NVM-03");
            var latestVersion = (await client.GetStringAsync("https://nodejs.org/dist/index.json"))
                .Split('\n')
                .Select(line => Regex.Match(line, "\\\"version\\\"\\s*:\\s*\\\"v(?<version>\\d+\\.\\d+\\.\\d+)\\\""))
                .Where(match => match.Success)
                .Select(match => Version.Parse(match.Groups["version"].Value))
                .OrderByDescending(version => version)
                .FirstOrDefault();

            LatestVersionHint.Text = latestVersion is null
                ? "LATEST NODE.JS VERSION UNAVAILABLE"
                : $"LATEST NODE.JS VERSION: {latestVersion}";
        }
        catch
        {
            LatestVersionHint.Text = "LATEST NODE.JS VERSION UNAVAILABLE";
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshVersionsAsync();

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var version = VersionInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            SetStatus("Enter a Node.js version to install.", true);
            return;
        }

        SetBusy(true, $"Installing Node.js {version}...");
        try
        {
            var result = await RunNvmAsync($"install {QuoteArgument(version)}");
            if (result.ExitCode != 0)
            {
                SetStatus(result.Output, true);
                return;
            }

            VersionInput.Clear();
            await RefreshVersionsAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Unable to install Node.js {version}: {exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || VersionsList.SelectedItem is not NodeVersion version)
        {
            return;
        }

        SetBusy(true, $"Activating Node.js {version.Version}...");
        try
        {
            var result = await RunNvmAsync($"use {QuoteArgument(version.Version)}");
            if (result.ExitCode != 0)
            {
                SetStatus(result.Output, true);
                return;
            }

            await RefreshVersionsAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Unable to activate Node.js {version.Version}: {exception.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void EditTagsButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not NodeVersion version)
        {
            return;
        }

        var currentTags = string.Join(", ", version.Tags ?? []);
        var editor = new TagEditorWindow(version.Version, currentTags) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        var normalizedTags = editor.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.ToUpperInvariant())
            .Where(tag => tag.Length <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (normalizedTags.Count == 0)
        {
            _tagsByVersion.Remove(version.Version);
        }
        else
        {
            _tagsByVersion[version.Version] = normalizedTags;
        }

        SaveTags(_tagsByVersion);
        var index = _versions.IndexOf(version);
        _versions[index] = version with { Tags = normalizedTags };
        SetStatus($"Saved tags for Node.js {version.Version}.", false);
    }

    private void VersionInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !InstallButton.IsEnabled)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            InstallButton_Click(sender, e);
        }
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isLoading = isBusy;
        RefreshButton.IsEnabled = !isBusy;
        InstallButton.IsEnabled = !isBusy;
        VersionsList.IsEnabled = !isBusy;
        VersionInput.IsEnabled = !isBusy;
        if (message is not null)
        {
            SetStatus(message, false);
        }
    }

    private void SetStatus(string status, bool isError)
    {
        StatusText.Text = status.Trim();
        StatusText.Foreground = isError ? System.Windows.Media.Brushes.IndianRed : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private static async Task<CommandResult> RunNvmAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "nvm",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start nvm. Ensure it is installed and available on PATH.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException("nvm did not finish within two minutes.");
        }

        return new CommandResult(process.ExitCode, (await outputTask + await errorTask).Trim());
    }

    private static IReadOnlyList<NodeVersion> ParseVersions(string output)
    {
        var versionMatches = Regex.Matches(output, @"(?m)^\s*(\*)?\s*(\d+\.\d+\.\d+)\b");
        return versionMatches
            .Select(match => new NodeVersion(match.Groups[2].Value, match.Groups[1].Success))
            .OrderByDescending(version => Version.Parse(version.Version))
            .ToList();
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "")}";

    private static Dictionary<string, List<string>> LoadTags()
    {
        try
        {
            var path = GetTagsPath();
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveTags(Dictionary<string, List<string>> tags)
    {
        var path = GetTagsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(tags, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetTagsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NVM-03", "tags.json");

    private sealed record NodeVersion(string Version, bool IsActive, IReadOnlyList<string>? Tags = null);

    private sealed record CommandResult(int ExitCode, string Output);
}
