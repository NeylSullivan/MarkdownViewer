using System.IO;
using System.Text.Json;
using MarkdownViewer.Models;

namespace MarkdownViewer.Services;

public sealed class SettingsStore
{
    private const int RecentLimit = 20;
    private const int PositionLimit = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    public SettingsStore(string? settingsDirectory = null)
    {
        var directory = settingsDirectory
            ?? Environment.GetEnvironmentVariable("MARKDOWNVIEWER_SETTINGS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer");
        FilePath = Path.Combine(directory, "settings.json");
    }

    public string FilePath { get; }
    public string? LastError { get; private set; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            LastError = null;
            try
            {
                if (!File.Exists(FilePath))
                    return new AppSettings();

                using var stream = File.OpenRead(FilePath);
                if (stream.Length > 1_048_576)
                    throw new JsonException("The settings file is too large.");

                var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
                Normalize(settings);
                return settings;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                LastError = "Could not load settings. Using the defaults. " + exception.Message;
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            LastError = null;
            string? temporaryPath = null;
            try
            {
                Normalize(settings);
                var directory = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, settings, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                LastError = "Could not save settings. " + exception.Message;
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    public void AddRecent(AppSettings settings, string path)
    {
        var fullPath = Path.GetFullPath(path);
        settings.RecentFiles.RemoveAll(item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, fullPath);
        if (settings.RecentFiles.Count > RecentLimit)
            settings.RecentFiles.RemoveRange(RecentLimit, settings.RecentFiles.Count - RecentLimit);
    }

    public void RememberScrollPosition(AppSettings settings, string path, double offset)
    {
        if (!double.IsFinite(offset) || offset < 0)
            return;

        var fullPath = Path.GetFullPath(path);
        var positions = settings.ScrollPositions
            .Where(pair => !string.Equals(pair.Key, fullPath, StringComparison.OrdinalIgnoreCase))
            .TakeLast(PositionLimit - 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        positions[fullPath] = offset;
        settings.ScrollPositions = positions;
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Theme = settings.Theme?.ToLowerInvariant() switch
        {
            "light" => "Light",
            "dark" => "Dark",
            _ => "System"
        };
        settings.FontSize = FiniteOrDefault(settings.FontSize, 16, 12, 28);
        settings.OutlineWidth = FiniteOrDefault(settings.OutlineWidth, 220, 150, 400);
        settings.ReadingWidth = FiniteOrDefault(settings.ReadingWidth, 860, 320, 1800);
        settings.WindowWidth = FiniteOrDefault(settings.WindowWidth, 1100, 640, 7680);
        settings.WindowHeight = FiniteOrDefault(settings.WindowHeight, 780, 400, 4320);
        settings.WindowLeft = ValidCoordinate(settings.WindowLeft);
        settings.WindowTop = ValidCoordinate(settings.WindowTop);

        settings.RecentFiles = (settings.RecentFiles ?? [])
            .Select(NormalizePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RecentLimit)
            .ToList();

        var positions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.ScrollPositions ?? [])
        {
            if (NormalizePath(pair.Key) is { } path && double.IsFinite(pair.Value) && pair.Value >= 0)
                positions[path] = pair.Value;
        }

        settings.ScrollPositions = positions.TakeLast(PositionLimit)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var documents = new List<OpenDocumentState>();
        var openPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in settings.OpenDocuments ?? [])
        {
            if (entry is null || NormalizePath(entry.FilePath) is not { } path || !openPaths.Add(path))
                continue;
            documents.Add(new OpenDocumentState
            {
                FilePath = path,
                ScrollOffset = double.IsFinite(entry.ScrollOffset) && entry.ScrollOffset >= 0 ? entry.ScrollOffset : 0
            });
        }
        settings.OpenDocuments = documents;
        var activePath = NormalizePath(settings.ActiveDocumentPath);
        settings.ActiveDocumentPath = documents.FirstOrDefault(entry => string.Equals(entry.FilePath, activePath, StringComparison.OrdinalIgnoreCase))?.FilePath;
    }

    private static double FiniteOrDefault(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static double? ValidCoordinate(double? value) =>
        value.HasValue && double.IsFinite(value.Value) && Math.Abs(value.Value) <= 100_000 ? value : null;

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;

        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }
}
