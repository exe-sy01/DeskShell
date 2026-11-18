using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskShell.Files;

/// <summary>
/// The side panel of the file explorer. "Preview" shows the selected file itself (a picture, the
/// start of a text file, a playable video or song, else its thumbnail); "Details" lists what the
/// file system knows about it. Everything slow happens off the UI thread and is dropped as soon as
/// the selection changes.
/// </summary>
public sealed class PreviewPane : Border
{
    readonly ToggleButton _previewChip, _detailsChip;
    readonly Border _body;
    string[] _paths = [];
    CancellationTokenSource? _cts;
    MediaView? _media;
    bool _showDetails, _rendered;

    /// <summary>Raised when you switch between Preview and Details, so the choice can be saved.</summary>
    public event Action? ModeChanged;

    public PreviewPane()
    {
        CornerRadius = new CornerRadius(16);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14);
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(BorderBrushProperty, "EdgeBrush");
        SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

        _previewChip = Chip("", "Preview");
        _detailsChip = Chip("", "Details");
        _previewChip.Margin = new Thickness(0, 0, 6, 0);
        _previewChip.Click += (_, _) => ShowDetails = false;
        _detailsChip.Click += (_, _) => ShowDetails = true;
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        chips.Children.Add(_previewChip);
        chips.Children.Add(_detailsChip);
        DockPanel.SetDock(chips, Dock.Top);

        _body = new Border { ClipToBounds = true };
        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(chips);
        root.Children.Add(_body);
        Child = root;

        SyncChips();
        ShowEmpty();
    }

    /// <summary>True for the Details list, false for the Preview. Setting it redraws the pane.</summary>
    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (_showDetails == value)
            {
                SyncChips(); // a click on the chip that was already on mustn't turn it off
                return;
            }
            _showDetails = value;
            SyncChips();
            ModeChanged?.Invoke();
            if (_paths.Length == 1) Render();
        }
    }

    /// <summary>Shows these items. Asking for the same items again does nothing (a video keeps playing).</summary>
    public void Show(IReadOnlyList<string> paths)
    {
        var next = paths.ToArray();
        if (_rendered && next.SequenceEqual(_paths, StringComparer.OrdinalIgnoreCase)) return;
        _paths = next;
        Render();
    }

    /// <summary>Shows the same items again, reading them afresh (after they changed on disk).</summary>
    public void Refresh() => Render();

    /// <summary>Stops any playback and lets go of pictures and files. Call when the pane is hidden.</summary>
    public void Clear()
    {
        CancelWork();
        _paths = [];
        _rendered = false;
        _body.Child = null;
    }

    // ---------- drawing ----------

    void Render()
    {
        CancelWork();
        _rendered = true;
        _body.Child = null;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        if (_paths.Length == 0) ShowEmpty();
        else if (_paths.Length > 1) PreviewBuilder.Run(ShowManyAsync(_paths, ct));
        else if (_showDetails) PreviewBuilder.Run(ShowDetailsAsync(_paths[0], ct));
        else
            PreviewBuilder.Run(PreviewBuilder.BuildAsync(_paths[0], PreviewScale.Pane,
                element => _body.Child = element, media => _media = media, ct));
    }

    void CancelWork()
    {
        _cts?.Cancel();
        _cts = null;
        _media?.Release();
        _media = null;
    }

    void SyncChips()
    {
        _previewChip.IsChecked = !_showDetails;
        _detailsChip.IsChecked = _showDetails;
    }

    void ShowEmpty()
    {
        var text = new TextBlock
        {
            Text = "Select a file to see it here",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        _body.Child = text;
    }

    /// <summary>"5 items selected" and how much they add up to (folders measured, so this can take a while).</summary>
    async Task ShowManyAsync(string[] paths, CancellationToken ct)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = PreviewBuilder.Glyph("", 40, new Thickness(0, 0, 0, 14));
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock
        {
            Text = $"{paths.Length} items selected",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var counts = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        counts.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        panel.Children.Add(counts);
        var size = new TextBlock { Text = "calculating…", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(size);
        _body.Child = panel;

        // Files add up at once; folders are measured one at a time so the disk isn't thrashed.
        var (fileBytes, files, folders) = await Task.Run(() =>
        {
            long bytes = 0;
            var fileCount = 0;
            var dirs = new List<string>();
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(path)) dirs.Add(path);
                    else
                    {
                        var info = new FileInfo(path);
                        if (info.Exists)
                        {
                            bytes += info.Length;
                            fileCount++;
                        }
                    }
                }
                catch
                {
                    // unreadable: leave it out of the total
                }
            }
            return (bytes, fileCount, dirs);
        }, ct);
        ct.ThrowIfCancellationRequested();

        counts.Text = folders.Count == 0 ? PreviewBuilder.Count(files, "file")
            : files == 0 ? PreviewBuilder.Count(folders.Count, "folder")
            : $"{PreviewBuilder.Count(files, "file")}, {PreviewBuilder.Count(folders.Count, "folder")}";

        var total = fileBytes;
        foreach (var folder in folders)
        {
            try { total += (await FolderSize.MeasureAsync(folder, ct)).Bytes; }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // a folder that can't be read counts as empty
            }
            ct.ThrowIfCancellationRequested();
        }
        size.Text = $"{FolderSize.Human(total)} in total";
    }

    /// <summary>A big thumbnail, the name, then a row for each fact; slow facts fill in as they arrive.</summary>
    async Task ShowDetailsAsync(string path, CancellationToken ct)
    {
        var icon = new Image
        {
            Width = 128,
            Height = 128,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 12)
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var title = new TextBlock
        {
            Text = PreviewBuilder.DisplayName(path),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        var rows = new Grid();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var stack = new StackPanel();
        stack.Children.Add(icon);
        stack.Children.Add(title);
        stack.Children.Add(rows);
        _body.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = stack
        };

        PreviewBuilder.Run(FillIconAsync(icon, path, ct));

        var facts = await Task.Run(() => PathFacts.Of(path), ct);
        ct.ThrowIfCancellationRequested();
        if (!facts.Exists)
        {
            AddRow(rows, "Location", Selectable(path));
            AddRow(rows, "Status", "This item can't be found or opened");
            return;
        }

        var kind = facts.IsFolder ? PreviewKind.Other : PreviewBuilder.KindOf(path);
        AddRow(rows, "Type", PreviewBuilder.TypeName(path, facts));

        var size = AddRow(rows, "Size", facts.IsFolder ? "calculating…" : SizeWithBytes(facts.Length));
        if (facts.IsDrive)
        {
            PreviewBuilder.Run(PreviewBuilder.Fill(size, Task.Run(() =>
            {
                var drive = new DriveInfo(path);
                return (string?)$"{FolderSize.Human(drive.AvailableFreeSpace)} free of {FolderSize.Human(drive.TotalSize)}";
            }, ct), ct));
        }
        else if (facts.IsFolder)
        {
            var contains = AddRow(rows, "Contains", "calculating…");
            PreviewBuilder.Run(FillFolderAsync(path, size, contains, ct));
        }

        if (kind == PreviewKind.Image)
        {
            var dimensions = AddRow(rows, "Dimensions", "…");
            PreviewBuilder.Run(PreviewBuilder.Fill(dimensions, Task.Run(() =>
                PreviewBuilder.ReadDimensions(path) is { } d ? $"{d.Width} × {d.Height} pixels" : (string?)null, ct), ct));
        }
        else if (kind is PreviewKind.Video or PreviewKind.Audio)
        {
            var duration = AddRow(rows, "Duration", "…");
            var dimensions = kind == PreviewKind.Video ? AddRow(rows, "Dimensions", "…") : null;
            PreviewBuilder.Run(FillMediaAsync(path, kind == PreviewKind.Video, duration, dimensions, ct));
        }

        AddRow(rows, "Created", PreviewBuilder.When(facts.Created));
        AddRow(rows, "Modified", PreviewBuilder.When(facts.Modified));
        AddRow(rows, "Accessed", PreviewBuilder.When(facts.Accessed));
        AddRow(rows, "Attributes", AttributeWords(facts.Attributes));
        var where = facts.IsDrive ? path : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;
        AddRow(rows, "Location", Selectable(where));
    }

    static async Task FillIconAsync(Image icon, string path, CancellationToken ct)
    {
        var image = await PreviewBuilder.ShellImageAsync(path, 128, ct);
        if (!ct.IsCancellationRequested) icon.Source = image;
    }

    static async Task FillFolderAsync(string path, TextBlock size, TextBlock contains, CancellationToken ct)
    {
        try
        {
            var (bytes, files, folders) = await FolderSize.MeasureAsync(path, ct);
            if (ct.IsCancellationRequested) return;
            size.Text = SizeWithBytes(bytes);
            contains.Text = $"{PreviewBuilder.Count(files, "file")}, {PreviewBuilder.Count(folders, "folder")}";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ct.IsCancellationRequested) return;
            size.Text = "—";
            contains.Text = "—";
        }
    }

    static async Task FillMediaAsync(string path, bool video, TextBlock duration, TextBlock? dimensions, CancellationToken ct)
    {
        try
        {
            var facts = await Task.Run(() => PreviewBuilder.MediaFactsAsync(path, video), ct);
            if (ct.IsCancellationRequested) return;
            duration.Text = facts.Duration > TimeSpan.Zero ? PreviewBuilder.Clock(facts.Duration) : "—";
            if (dimensions != null) dimensions.Text = facts.Width > 0 && facts.Height > 0 ? $"{facts.Width} × {facts.Height}" : "—";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ct.IsCancellationRequested) return;
            duration.Text = "—";
            if (dimensions != null) dimensions.Text = "—";
        }
    }

    // ---------- pieces ----------

    static ToggleButton Chip(string glyph, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(PreviewBuilder.Glyph(glyph, 12, new Thickness(0, 0, 6, 0)));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var chip = new ToggleButton { Content = content, Focusable = false };
        chip.SetResourceReference(StyleProperty, "ChipButton");
        return chip;
    }

    static TextBlock AddRow(Grid grid, string label, string value)
    {
        var text = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        AddRow(grid, label, text);
        return text;
    }

    static void AddRow(Grid grid, string label, FrameworkElement value)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 14, 9) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        value.Margin = new Thickness(0, 0, 0, 9);
        value.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(name, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
    }

    /// <summary>Text you can select and copy (the location), dressed as a plain label.</summary>
    static TextBox Selectable(string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = 12
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrush");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
        return box;
    }

    static string SizeWithBytes(long bytes) =>
        bytes < 1024 ? FolderSize.Human(bytes) : $"{FolderSize.Human(bytes)} ({bytes:N0} bytes)";

    static string AttributeWords(FileAttributes attributes)
    {
        var words = new List<string>();
        if ((attributes & FileAttributes.ReadOnly) != 0) words.Add("Read-only");
        if ((attributes & FileAttributes.Hidden) != 0) words.Add("Hidden");
        if ((attributes & FileAttributes.System) != 0) words.Add("System");
        if ((attributes & FileAttributes.Archive) != 0) words.Add("Archive");
        return words.Count == 0 ? "None" : string.Join(", ", words);
    }
}

// ======================================================================================
// Shared by the preview pane and quick look.
// ======================================================================================

internal enum PreviewKind { Other, Image, Text, Video, Audio }

/// <summary>How big things get: the side pane shows small versions, quick look large ones.</summary>
internal readonly record struct PreviewScale(int ImageWidth, int ImageHeight, int TextBytes, double TextSize, int ThumbSize, double PlaySize)
{
    public static readonly PreviewScale Pane = new(600, int.MaxValue, 64 * 1024, 12, 256, 40);
    public static readonly PreviewScale Large = new(1600, 1600, 256 * 1024, 13, 1024, 48);
}

/// <summary>What one quick look at the file system says about a path.</summary>
internal sealed record PathFacts(bool Exists, bool IsFolder, bool IsDrive, long Length, DateTime Created, DateTime Modified, DateTime Accessed, FileAttributes Attributes)
{
    public static readonly PathFacts Missing = new(false, false, false, 0, default, default, default, 0);

    /// <summary>Reads the facts; touches the disk (or network), so call it off the UI thread.</summary>
    public static PathFacts Of(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) return Missing;
            var folder = info as DirectoryInfo;
            DateTime Safe(Func<DateTime> read)
            {
                try { return read(); }
                catch { return default; }
            }
            FileAttributes attributes;
            try { attributes = info.Attributes; }
            catch { attributes = 0; }
            return new PathFacts(true, folder != null, folder is { Parent: null }, info is FileInfo file ? file.Length : 0,
                Safe(() => info.CreationTime), Safe(() => info.LastWriteTime), Safe(() => info.LastAccessTime), attributes);
        }
        catch
        {
            return Missing;
        }
    }
}

/// <summary>Reads files into something to look at: pictures, text, media and shell thumbnails.</summary>
internal static class PreviewBuilder
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff" };
    static readonly HashSet<string> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".cs", ".js", ".ts", ".py", ".log", ".ini", ".cfg", ".yaml", ".yml", ".csv", ".html",
        ".css", ".ps1", ".bat", ".cmd", ".c", ".cpp", ".h", ".java", ".rs", ".go", ".sql", ".toml", ".xaml", ".csproj"
    };
    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv" };
    static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" };

    /// <summary>How a file (never a folder) is previewed, by its extension.</summary>
    public static PreviewKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return PreviewKind.Image;
        if (Texts.Contains(ext)) return PreviewKind.Text;
        if (Videos.Contains(ext)) return PreviewKind.Video;
        if (Audios.Contains(ext)) return PreviewKind.Audio;
        return PreviewKind.Other;
    }

    /// <summary>
    /// Fills an area with the best preview of one item. <paramref name="show"/> receives the
    /// element; <paramref name="mediaShown"/> the player, which the caller must release when it
    /// moves on. Throws OperationCanceledException when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task BuildAsync(string path, PreviewScale scale, Action<UIElement?> show, Action<MediaView> mediaShown, CancellationToken ct)
    {
        var facts = await Task.Run(() => PathFacts.Of(path), ct);
        ct.ThrowIfCancellationRequested();
        var kind = facts.Exists && !facts.IsFolder ? KindOf(path) : PreviewKind.Other;
        switch (kind)
        {
            case PreviewKind.Image:
            {
                var image = await Task.Run(() => DecodeImage(path, scale.ImageWidth, scale.ImageHeight), ct);
                ct.ThrowIfCancellationRequested();
                if (image != null)
                {
                    show(ImageView(image));
                    return;
                }
                break; // not decodable here: fall back to the shell's thumbnail
            }
            case PreviewKind.Text:
            {
                var text = await Task.Run(() => ReadText(path, scale.TextBytes), ct);
                ct.ThrowIfCancellationRequested();
                if (text != null)
                {
                    show(TextView(text, scale.TextSize));
                    return;
                }
                break;
            }
            case PreviewKind.Video:
            case PreviewKind.Audio:
            {
                var media = new MediaView(path, scale.PlaySize);
                show(media);
                mediaShown(media);
                var poster = await ShellImageAsync(path, Math.Min(scale.ThumbSize, 512), ct);
                ct.ThrowIfCancellationRequested();
                media.Poster = poster;
                return;
            }
        }

        var name = DisplayName(path);
        var type = facts.Exists ? TypeName(path, facts) : "Can't be found";
        var thumb = await ShellImageAsync(path, scale.ThumbSize, ct);
        ct.ThrowIfCancellationRequested();
        show(IconCard(thumb, name, type, scale.ThumbSize));
    }

    // ---------- reading ----------

    /// <summary>
    /// Decodes a picture no bigger than needed (so a 50-megapixel photo doesn't take 200 MB),
    /// frozen so the UI thread can use it. Null if it can't be read.
    /// </summary>
    public static BitmapSource? DecodeImage(string path, int maxWidth, int maxHeight)
    {
        try
        {
            if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
            {
                // Icons hold several sizes; show the biggest rather than the first (often 16px).
                using var iconStream = OpenShared(path);
                var decoder = BitmapDecoder.Create(iconStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var best = decoder.Frames.MaxBy(f => f.PixelWidth) ?? decoder.Frames[0];
                best.Freeze();
                return best;
            }

            var size = ReadDimensions(path);
            using var stream = OpenShared(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            if (size is { } s && s.Width > 0 && s.Height > 0)
            {
                var factor = Math.Min(1.0, Math.Min((double)maxWidth / s.Width, (double)maxHeight / s.Height));
                if (factor < 1) bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(s.Width * factor));
            }
            else bitmap.DecodePixelWidth = maxWidth; // size unknown: at least don't decode it huge
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A picture's size in pixels, read from its header without decoding it.</summary>
    public static (int Width, int Height)? ReadDimensions(string path)
    {
        try
        {
            using var stream = OpenShared(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The start of a text file, or null if it can't be read or looks binary.</summary>
    public static string? ReadText(string path, int limit)
    {
        try
        {
            using var stream = OpenShared(path);
            var length = stream.Length;
            var buffer = new byte[(int)Math.Min(limit, Math.Max(0, length))];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (text.Contains('\0')) return null;
            if (length > limit) text += $"\n\n… only the first {FolderSize.Human(limit)} is shown";
            return text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The shell's thumbnail (or icon) for a path, made on an STA thread as thumbnail handlers expect.</summary>
    public static Task<ImageSource?> ShellImageAsync(string path, int size, CancellationToken ct) =>
        StaRunner.Run(() => IconLoader.ShellImage(path, size), ct);

    /// <summary>Length (and for video, frame size) from the Windows property system.</summary>
    public static async Task<(TimeSpan Duration, uint Width, uint Height)> MediaFactsAsync(string path, bool video)
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        if (video)
        {
            var properties = await file.Properties.GetVideoPropertiesAsync();
            return (properties.Duration, properties.Width, properties.Height);
        }
        var music = await file.Properties.GetMusicPropertiesAsync();
        return (music.Duration, 0, 0);
    }

    static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // ---------- views ----------

    public static Image ImageView(ImageSource? image)
    {
        var view = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
        return view;
    }

    /// <summary>Read-only monospaced text that scrolls both ways (code shouldn't wrap).</summary>
    public static UIElement TextView(string text, double fontSize)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            FontFamily = new FontFamily("Consolas"),
            FontSize = fontSize,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(4)
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrush");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
        var frame = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Child = box };
        frame.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        return frame;
    }

    /// <summary>The thumbnail with the name and type under it, shrunk to fit the space it's given.</summary>
    public static UIElement IconCard(ImageSource? thumb, string name, string type, int size)
    {
        var image = new Image
        {
            Source = thumb,
            MaxWidth = size,
            MaxHeight = size,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var title = new TextBlock
        {
            Text = name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
        var kind = new TextBlock { Text = type, FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        stack.Children.Add(image);
        stack.Children.Add(title);
        stack.Children.Add(kind);
        // A stack gives the picture unlimited height, so cap it by the room left over for it.
        var host = new Grid { Children = { stack } };
        host.SizeChanged += (_, e) =>
            image.MaxHeight = Math.Max(32, Math.Min(size, e.NewSize.Height - title.ActualHeight - kind.ActualHeight - 40));
        return host;
    }

    public static TextBlock Glyph(string glyph, double size, Thickness margin = default)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontSize = size,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return text;
    }

    // ---------- words ----------

    /// <summary>The name to show: the last part of the path, or the whole thing for a drive.</summary>
    public static string DisplayName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }

    /// <summary>"Drive", "File folder", or the registered type name. Call on the UI thread (cached per extension).</summary>
    public static string TypeName(string path, PathFacts facts) =>
        facts.IsDrive ? "Drive" : facts.IsFolder ? "File folder" : FileTypes.NameOf(Path.GetExtension(path));

    public static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    /// <summary>A date in the user's short format; "—" for none (file times before 1700 mean unset).</summary>
    public static string When(DateTime time) => time == default || time.Year < 1700 ? "—" : time.ToString("g");

    /// <summary>"3:07" or "1:02:45".</summary>
    public static string Clock(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    // ---------- tasks ----------

    /// <summary>Runs UI work started without awaiting: cancellation is expected, other errors are logged, never thrown.</summary>
    public static async void Run(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Preview failed", ex); }
    }

    /// <summary>Puts a slow value into a label when it arrives; "—" if it can't be had.</summary>
    public static async Task Fill(TextBlock target, Task<string?> work, CancellationToken ct)
    {
        string? text;
        try { text = await work; }
        catch (OperationCanceledException) { return; }
        catch { text = null; }
        if (!ct.IsCancellationRequested) target.Text = string.IsNullOrEmpty(text) ? "—" : text;
    }
}

/// <summary>
/// One background STA thread for shell thumbnails in previews, kept apart from the icon loader's
/// threads so a slow thumbnail handler there can't hold up the preview (or the other way round).
/// Work whose request has been cancelled by the time it comes up is skipped.
/// </summary>
internal static class StaRunner
{
    static readonly BlockingCollection<Action> Queue = new();

    static StaRunner()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Log.Error("Preview thumbnail failed", ex); }
            }
        }) { IsBackground = true, Name = "DeskShell preview" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static Task<T> Run<T>(Func<T> work, CancellationToken ct)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                done.TrySetCanceled(ct);
                return;
            }
            try { done.TrySetResult(work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }
}

/// <summary>
/// A video or song: the shell's thumbnail as a poster, then a round play/pause button, a
/// progress bar and the time. Nothing is opened until play is pressed, and <see cref="Release"/>
/// lets go of the file.
/// </summary>
internal sealed class MediaView : Grid
{
    readonly string _path;
    readonly Image _poster;
    readonly MediaElement _media;
    readonly RoundButton _play;
    readonly Slider _progress;
    readonly TextBlock _time;
    readonly DispatcherTimer _timer;
    bool _playing, _released, _updating;

    public MediaView(string path, double buttonSize)
    {
        _path = path;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _poster = PreviewBuilder.ImageView(null);
        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Hidden
        };
        _media.MediaOpened += (_, _) =>
        {
            if (_media.NaturalDuration.HasTimeSpan) _progress!.Maximum = Math.Max(0.1, _media.NaturalDuration.TimeSpan.TotalSeconds);
            if (_media.HasVideo) _poster.Visibility = Visibility.Collapsed; // a song keeps its cover
            UpdateProgress();
        };
        _media.MediaEnded += (_, _) =>
        {
            _media.Stop();
            SetPlaying(false);
            _media.Visibility = Visibility.Hidden;
            _poster.Visibility = Visibility.Visible;
            UpdateProgress();
        };
        _media.MediaFailed += (_, _) =>
        {
            SetPlaying(false);
            _play!.IsEnabled = false;
            _play.Opacity = 0.4;
            _time!.Text = "Can't play this file";
        };

        // Clicking the picture plays and pauses too.
        var stage = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        stage.Children.Add(_poster);
        stage.Children.Add(_media);
        stage.MouseLeftButtonUp += (_, _) => Toggle();
        Children.Add(stage);

        _play = new RoundButton("", buttonSize) { Margin = new Thickness(0, 0, 10, 0) };
        _play.Click += Toggle;
        _time = new TextBlock { Text = "0:00", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        _time.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        _progress = new Slider { Minimum = 0, Maximum = 1, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        _progress.SetResourceReference(StyleProperty, "FlatSlider");
        _progress.ValueChanged += (_, e) =>
        {
            if (_updating || _released || _media.Source == null) return;
            _media.Position = TimeSpan.FromSeconds(e.NewValue);
            UpdateTime();
        };
        var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        DockPanel.SetDock(_play, Dock.Left);
        DockPanel.SetDock(_time, Dock.Right);
        bar.Children.Add(_play);
        bar.Children.Add(_time);
        bar.Children.Add(_progress);
        SetRow(bar, 1);
        Children.Add(bar);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdateProgress();

        // Taken out of the window some other way: never keep playing where it can't be seen.
        Unloaded += (_, _) => Release();
    }

    public ImageSource? Poster
    {
        get => _poster.Source;
        set => _poster.Source = value;
    }

    public void Toggle()
    {
        if (_released || !_play.IsEnabled) return;
        if (_playing)
        {
            _media.Pause();
            SetPlaying(false);
            return;
        }
        if (_media.Source == null) _media.Source = ToUri(_path);
        _media.Visibility = Visibility.Visible;
        _media.Play();
        SetPlaying(true);
    }

    /// <summary>Stops playback and closes the file. The view is dead afterwards.</summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        _timer.Stop();
        try
        {
            _media.Stop();
            _media.Close();
        }
        catch
        {
            // already closed
        }
        _media.Source = null;
        _poster.Source = null;
    }

    void SetPlaying(bool playing)
    {
        _playing = playing;
        _play.Glyph = playing ? "" : "";
        if (playing) _timer.Start();
        else _timer.Stop();
    }

    void UpdateProgress()
    {
        if (_released) return;
        _updating = true;
        _progress.Value = Math.Min(_progress.Maximum, _media.Position.TotalSeconds);
        _updating = false;
        UpdateTime();
    }

    void UpdateTime()
    {
        var position = PreviewBuilder.Clock(_media.Position);
        _time.Text = _media.NaturalDuration.HasTimeSpan ? $"{position} / {PreviewBuilder.Clock(_media.NaturalDuration.TimeSpan)}" : position;
    }

    /// <summary>A file URI that survives '#' in the path (new Uri(path) would treat it as a fragment).</summary>
    static Uri ToUri(string path) =>
        path.Contains('#') ? new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = path }.Uri : new Uri(path);
}

/// <summary>A round glyph button drawn in theme colours (for play/pause).</summary>
internal sealed class RoundButton : Border
{
    readonly TextBlock _glyph;

    public event Action? Click;

    public RoundButton(string glyph, double size)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BackgroundProperty, "RaisedBrush");
        SetResourceReference(BorderBrushProperty, "EdgeBrush");
        _glyph = PreviewBuilder.Glyph(glyph, Math.Round(size * 0.36));
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Child = _glyph;

        MouseEnter += (_, _) => SetResourceReference(BackgroundProperty, "RaisedHoverBrush");
        MouseLeave += (_, _) => SetResourceReference(BackgroundProperty, "RaisedBrush");
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CaptureMouse();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            e.Handled = true;
            var at = e.GetPosition(this);
            if (IsEnabled && at.X >= 0 && at.Y >= 0 && at.X <= ActualWidth && at.Y <= ActualHeight) Click?.Invoke();
        };
    }

    public string Glyph
    {
        get => _glyph.Text;
        set => _glyph.Text = value;
    }
}
