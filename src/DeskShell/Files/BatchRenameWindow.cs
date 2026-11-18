using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using DeskShell.WindowManagement;

namespace DeskShell.Files;

/// <summary>
/// Renames many files at once, by find and replace or by a pattern like "Holiday {n:000}". Every
/// change shows in a live preview first, and anything that would go wrong (empty names, characters
/// Windows refuses, two files ending up with the same name) is pointed out before anything is touched.
/// Show it with ShowDialog; on success <see cref="RenamedPaths"/> holds the new paths.
/// </summary>
public sealed class BatchRenameWindow : Window
{
    /// <summary>One file or folder being renamed. Its path moves on if a rename only partly succeeds.</summary>
    sealed class Item(string path, bool isFolder, DateTime modified)
    {
        public string Path = path;
        public readonly bool IsFolder = isFolder;
        public readonly DateTime Modified = modified;
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public string Name => System.IO.Path.GetFileName(Path);
    }

    /// <summary>A line of the preview. State is a string ("Same", "Changed", "Problem") so the template can trigger on it.</summary>
    public sealed class PreviewRow : INotifyPropertyChanged
    {
        string _oldName = "", _newName = "", _problem = "", _state = "Same";
        public string OldName { get => _oldName; set => Set(ref _oldName, value); }
        public string NewName { get => _newName; set => Set(ref _newName, value); }
        public string Problem { get => _problem; set => Set(ref _problem, value); }
        public string State { get => _state; set => Set(ref _state, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    enum CaseChange { Keep, Lower, Upper, Title }

    static readonly Regex Tokens = new(@"\{(?<token>name|n|ext|date|parent)(?::(?<pad>0+))?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    readonly List<Item> _items = new();
    readonly List<PreviewRow> _rows = new();
    readonly List<string> _renamed = new();

    readonly RadioButton _findMode, _patternMode;
    readonly FrameworkElement _findPanel, _patternPanel;
    readonly TextBox _find, _replace, _pattern, _startAt;
    readonly CheckBox _matchCase, _useRegex, _keepExtension;
    readonly RadioButton[] _caseChips;
    readonly TextBlock _regexError, _summary, _errors;
    readonly Button _rename, _cancel;
    CaseChange _case = CaseChange.Keep;
    bool _building = true;
    bool _canRename;

    /// <summary>The new full paths of everything that was renamed (also filled after a partial failure).</summary>
    public IReadOnlyList<string> RenamedPaths => _renamed;

    public BatchRenameWindow(IReadOnlyList<string> paths)
    {
        ThemeManager.Use(Resources);
        Closed += (_, _) => ThemeManager.Release(Resources);
        SourceInitialized += (_, _) =>
        {
            // Title bar follows the theme's light/dark, like the rest of the window.
            var on = ThemeManager.IsDark ? 1 : 0;
            WmNative.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        };

        foreach (var raw in paths)
        {
            var path = raw.TrimEnd('\\', '/');
            if (Path.GetFileName(path).Length == 0) continue; // a drive can't be renamed here
            var isFolder = Directory.Exists(path);
            DateTime modified;
            try { modified = isFolder ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path); }
            catch { modified = DateTime.Now; }
            _items.Add(new Item(path, isFolder, modified));
            _rows.Add(new PreviewRow());
        }

        Title = _items.Count == 1 ? "Rename 1 item" : $"Rename {_items.Count} items";
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;

        // ---------- mode ----------
        _findMode = Chip("Find & replace", "mode");
        _patternMode = Chip("Pattern", "mode");
        _findMode.IsChecked = true;
        var modes = Row(_findMode, _patternMode);

        // ---------- find & replace ----------
        _find = Field();
        _replace = Field();
        _matchCase = Switch("Match case");
        _useRegex = Switch("Use regular expressions");
        _regexError = Quiet("");
        _regexError.Visibility = Visibility.Collapsed;
        var findGrid = new Grid();
        findGrid.ColumnDefinitions.Add(new ColumnDefinition());
        findGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        findGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var findColumn = Labelled("Find", _find);
        var replaceColumn = Labelled("Replace with", _replace);
        Grid.SetColumn(replaceColumn, 2);
        findGrid.Children.Add(findColumn);
        findGrid.Children.Add(replaceColumn);
        var switches = Row(_matchCase, _useRegex);
        switches.Margin = new Thickness(0, 12, 0, 0);
        _matchCase.Margin = new Thickness(0, 0, 24, 0);
        _findPanel = new StackPanel { Children = { findGrid, switches, _regexError } };
        _regexError.Margin = new Thickness(0, 8, 0, 0);

        // ---------- pattern ----------
        _pattern = Field("{name}");
        _startAt = Field("1");
        _startAt.Width = 90;
        var patternGrid = new Grid();
        patternGrid.ColumnDefinitions.Add(new ColumnDefinition());
        patternGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        patternGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var patternColumn = Labelled("Name", _pattern);
        var startColumn = Labelled("Start at", _startAt);
        Grid.SetColumn(startColumn, 2);
        patternGrid.Children.Add(patternColumn);
        patternGrid.Children.Add(startColumn);
        var hint = Quiet("{name} original name   {n} or {n:000} number   {ext} extension   {date} modified date   {parent} folder");
        hint.Margin = new Thickness(0, 8, 0, 0);
        hint.TextWrapping = TextWrapping.Wrap;
        _patternPanel = new StackPanel { Children = { patternGrid, hint }, Visibility = Visibility.Collapsed };

        // ---------- shared ----------
        _caseChips = [Chip("Keep", "case"), Chip("lower", "case"), Chip("UPPER", "case"), Chip("Title", "case")];
        _caseChips[0].IsChecked = true;
        for (var i = 0; i < _caseChips.Length; i++)
        {
            var value = (CaseChange)i;
            _caseChips[i].Checked += (_, _) => { _case = value; Refresh(); };
        }
        var caseLabel = Quiet("Change case");
        caseLabel.VerticalAlignment = VerticalAlignment.Center;
        caseLabel.Margin = new Thickness(0, 0, 10, 0);
        _keepExtension = Switch("Keep the extension");
        _keepExtension.IsChecked = true;
        _keepExtension.Margin = new Thickness(24, 0, 0, 0);
        var shared = Row([caseLabel, .. _caseChips, _keepExtension]);
        shared.Margin = new Thickness(0, 16, 0, 0);

        // ---------- preview ----------
        var previewTitle = new TextBlock { Text = "Preview", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _summary = Quiet("");
        _summary.Margin = new Thickness(12, 0, 0, 0);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        var previewHeader = Row(previewTitle, _summary);
        previewHeader.Margin = new Thickness(0, 20, 0, 8);

        var list = new ListBox
        {
            ItemsSource = _rows,
            ItemTemplate = RowTemplate.Value,
            ItemContainerStyle = RowContainer.Value,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Padding = new Thickness(0, 6, 0, 6)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var listCard = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = list };
        listCard.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        listCard.SetResourceReference(Border.BorderBrushProperty, "EdgeBrush");

        // ---------- buttons ----------
        _errors = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed };
        _errors.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        // Not IsCancel: that would set DialogResult = false after our own handler already closed the window.
        _cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        _cancel.SetResourceReference(StyleProperty, "PillButton");
        _cancel.Click += (_, _) => Leave();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Leave();
        };
        _rename = new Button { Content = "Rename", IsDefault = true, MinWidth = 110 };
        _rename.SetResourceReference(StyleProperty, "AccentButton");
        _rename.Click += (_, _) => RenameAll();
        var buttons = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_cancel, 1);
        Grid.SetColumn(_rename, 2);
        buttons.Children.Add(_errors);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_rename);

        // ---------- layout ----------
        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        for (var i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Insert(5, new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        modes.Margin = new Thickness(0, 0, 0, 14);
        var modeArea = new Grid { Children = { _findPanel, _patternPanel } };
        UIElement[] rows = [heading, modes, modeArea, shared, previewHeader, listCard, buttons];
        for (var i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i], i);
            root.Children.Add(rows[i]);
        }
        Content = root;

        // ---------- live updates ----------
        _findMode.Checked += (_, _) => SwitchMode();
        _patternMode.Checked += (_, _) => SwitchMode();
        foreach (var box in new[] { _find, _replace, _pattern, _startAt }) box.TextChanged += (_, _) => Refresh();
        foreach (var check in new[] { _matchCase, _useRegex, _keepExtension })
        {
            check.Checked += (_, _) => Refresh();
            check.Unchecked += (_, _) => Refresh();
        }

        Loaded += (_, _) =>
        {
            // Without an owner, CenterOwner has nothing to centre on.
            if (Owner == null)
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - ActualWidth) / 2;
                Top = area.Top + (area.Height - ActualHeight) / 2;
            }
            _find.Focus();
        };

        _building = false;
        Refresh();
    }

    // ---------- working out the new names ----------

    void SwitchMode()
    {
        if (_building) return;
        var pattern = _patternMode.IsChecked == true;
        _findPanel.Visibility = pattern ? Visibility.Collapsed : Visibility.Visible;
        _patternPanel.Visibility = pattern ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.InvokeAsync(() =>
        {
            var box = pattern ? _pattern : _find;
            box.Focus();
            box.CaretIndex = box.Text.Length;
        });
        Refresh();
    }

    /// <summary>Recomputes every new name and every problem, and whether Rename can go ahead.</summary>
    void Refresh()
    {
        if (_building) return;
        var names = ComputeNames(out var regexError);
        _regexError.Text = regexError ?? "";
        _regexError.Visibility = regexError == null ? Visibility.Collapsed : Visibility.Visible;

        // Who would end up where, to spot two items landing on the same name.
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _items.Count; i++)
        {
            var key = Path.Combine(_items[i].Folder, names[i]);
            targets[key] = targets.GetValueOrDefault(key) + 1;
        }
        var current = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);

        int changed = 0, problems = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var row = _rows[i];
            var name = names[i];
            var isChanged = !string.Equals(name, item.Name, StringComparison.Ordinal);
            var problem = isChanged ? ProblemWith(name, item, targets, current) : null;
            // A duplicate also matters for an item that isn't changing: something else is landing on it.
            if (problem == null && targets.GetValueOrDefault(Path.Combine(item.Folder, name)) > 1)
                problem = "Another item would get the same name.";

            row.OldName = item.Name;
            row.NewName = name.Length == 0 ? "(empty)" : name;
            row.Problem = problem ?? "";
            row.State = problem != null ? "Problem" : isChanged ? "Changed" : "Same";
            if (isChanged) changed++;
            if (problem != null) problems++;
        }

        _summary.Text = (changed, problems) switch
        {
            (0, 0) => "Nothing changes yet",
            (_, 0) => changed == 1 ? "1 item will be renamed" : $"{changed} items will be renamed",
            _ => problems == 1 ? "1 problem to fix first" : $"{problems} problems to fix first"
        };
        _canRename = changed > 0 && problems == 0 && regexError == null;
        _rename.IsEnabled = _canRename;
        _rename.Content = changed > 0 ? $"Rename {changed}" : "Rename";
    }

    /// <summary>The new name for each item, in order. A bad regular expression leaves every name as it was.</summary>
    string[] ComputeNames(out string? regexError)
    {
        regexError = null;
        var keepExtension = _keepExtension.IsChecked == true;
        var result = new string[_items.Count];

        Regex? regex = null;
        var pattern = _patternMode.IsChecked == true;
        var find = _find.Text;
        if (!pattern && find.Length > 0 && _useRegex.IsChecked == true)
        {
            try
            {
                regex = new Regex(find, (_matchCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                regexError = "That regular expression isn't valid: " + ex.Message;
                return _items.Select(i => i.Name).ToArray();
            }
        }
        if (!int.TryParse(_startAt.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var start)) start = 1;

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            // Folders have no extension: a dot in "Project.v2" is part of the name.
            var hasExtension = !item.IsFolder;
            var stem = hasExtension ? Path.GetFileNameWithoutExtension(item.Name) : item.Name;
            var extension = hasExtension ? Path.GetExtension(item.Name) : "";
            var editsWholeName = !keepExtension || extension.Length == 0;
            var editable = editsWholeName ? item.Name : stem;

            string core;
            if (pattern)
                core = ApplyPattern(_pattern.Text, item, stem, extension, start + i);
            else if (find.Length == 0)
                core = editable;
            else if (regex != null)
            {
                try { core = regex.Replace(editable, _replace.Text); }
                catch (RegexMatchTimeoutException)
                {
                    regexError = "That regular expression takes too long to run.";
                    return _items.Select(x => x.Name).ToArray();
                }
                catch (ArgumentException ex)
                {
                    // e.g. a bad "$" group reference in the replacement
                    regexError = "The replacement isn't valid: " + ex.Message;
                    return _items.Select(x => x.Name).ToArray();
                }
            }
            else
                core = editable.Replace(find, _replace.Text, _matchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

            core = _case switch
            {
                CaseChange.Lower => core.ToLower(CultureInfo.CurrentCulture),
                CaseChange.Upper => core.ToUpper(CultureInfo.CurrentCulture),
                // ToTitleCase leaves ALL-CAPS words alone, so lower everything first.
                CaseChange.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(core.ToLower(CultureInfo.CurrentCulture)),
                _ => core
            };
            // Windows drops leading and trailing spaces anyway (and FileOps.Rename trims), so the preview does too.
            result[i] = (editsWholeName ? core : core + extension).Trim();
        }
        return result;
    }

    static string ApplyPattern(string pattern, Item item, string stem, string extension, int number) =>
        Tokens.Replace(pattern, m => m.Groups["token"].Value.ToLowerInvariant() switch
        {
            "name" => stem,
            "n" => m.Groups["pad"].Success ? number.ToString(m.Groups["pad"].Value, CultureInfo.InvariantCulture) : number.ToString(CultureInfo.InvariantCulture),
            "ext" => extension.TrimStart('.'),
            "date" => item.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "parent" => Path.GetFileName(item.Folder.TrimEnd('\\', '/')),
            _ => m.Value
        });

    /// <summary>Why a new name can't be used, or null if it's fine.</summary>
    static string? ProblemWith(string name, Item item, Dictionary<string, int> targets, HashSet<string> current)
    {
        if (name.Length == 0) return "The name would be empty.";
        if (name.IndexOfAny(Invalid) >= 0) return "A name can't contain \\ / : * ? \" < > |";
        if (name.EndsWith('.')) return "A name can't end with a dot.";
        var beforeDot = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(beforeDot)) return $"\"{beforeDot}\" is a name Windows keeps for itself.";
        var target = Path.Combine(item.Folder, name);
        if (targets.GetValueOrDefault(target) > 1) return "Another item would get the same name.";
        // Another item of the batch is fine (it moves out of the way); anything else would be overwritten.
        if (!current.Contains(target) && (File.Exists(target) || Directory.Exists(target)))
            return "There's already something called that here.";
        return null;
    }

    // ---------- renaming ----------

    void RenameAll()
    {
        Refresh();
        if (!_canRename) return;
        var names = ComputeNames(out _);
        var changes = Enumerable.Range(0, _items.Count)
            .Where(i => !string.Equals(names[i], _items[i].Name, StringComparison.Ordinal))
            .Select(i => (Item: _items[i], NewName: names[i]))
            .ToList();
        if (changes.Count == 0) return;

        // If any new name is something in the batch right now (a swap, a chain like 1→2, 2→3, or just a
        // change of case), renaming one by one would collide. Moving everything to a temporary name first
        // clears the way.
        var current = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var twoStep = changes.Any(c => current.Contains(Path.Combine(c.Item.Folder, c.NewName)));

        var errors = new List<string>();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            if (!twoStep)
            {
                foreach (var (item, newName) in changes)
                {
                    var error = FileOps.Rename(item.Path, newName);
                    if (error != null) errors.Add($"{item.Name}: {error}");
                    else Moved(item, newName);
                }
            }
            else
            {
                var parked = new List<(Item Item, string NewName, string OldName)>();
                var stamp = Guid.NewGuid().ToString("N")[..8];
                for (var i = 0; i < changes.Count; i++)
                {
                    var (item, newName) = changes[i];
                    var oldName = item.Name;
                    var temporary = $"~rename-{stamp}-{i}";
                    var error = FileOps.Rename(item.Path, temporary);
                    if (error != null) { errors.Add($"{oldName}: {error}"); continue; }
                    item.Path = Path.Combine(item.Folder, temporary);
                    parked.Add((item, newName, oldName));
                }
                foreach (var (item, newName, oldName) in parked)
                {
                    var error = FileOps.Rename(item.Path, newName);
                    if (error == null) { Moved(item, newName); continue; }
                    // Put it back rather than leave it under the temporary name.
                    var restore = FileOps.Rename(item.Path, oldName);
                    if (restore == null)
                    {
                        item.Path = Path.Combine(item.Folder, oldName);
                        errors.Add($"{oldName}: {error}");
                    }
                    else errors.Add($"{oldName}: {error} It's now called \"{item.Name}\".");
                }
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (errors.Count == 0)
        {
            DialogResult = true;
            return;
        }

        // Some went wrong: show why, and carry on from how things are now so another try starts fresh.
        _errors.Text = errors.Count == 1 ? errors[0] : $"{errors.Count} couldn't be renamed. {errors[0]}";
        _errors.ToolTip = string.Join("\n", errors);
        _errors.Visibility = Visibility.Visible;
        _cancel.Content = "Close";
        Refresh();
    }

    /// <summary>Cancel / Close / Escape. After a partial failure some items did get new names, so that counts as a result.</summary>
    void Leave() => DialogResult = _renamed.Count > 0;

    void Moved(Item item, string newName)
    {
        item.Path = Path.Combine(item.Folder, newName);
        _renamed.Add(item.Path);
    }

    // ---------- building blocks ----------

    RadioButton Chip(string text, string group)
    {
        var chip = new RadioButton { Content = text, GroupName = group + GetHashCode(), Margin = new Thickness(0, 0, 6, 0) };
        chip.SetResourceReference(StyleProperty, "ChipButton");
        return chip;
    }

    TextBox Field(string text = "")
    {
        var box = new TextBox { Text = text };
        box.SetResourceReference(StyleProperty, "Field");
        return box;
    }

    CheckBox Switch(string text)
    {
        var box = new CheckBox { Content = text };
        box.SetResourceReference(StyleProperty, "Switch");
        return box;
    }

    static TextBlock Quiet(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        return block;
    }

    static StackPanel Labelled(string label, UIElement field)
    {
        var caption = Quiet(label);
        caption.Margin = new Thickness(0, 0, 0, 6);
        return new StackPanel { Children = { caption, field } };
    }

    static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    /// <summary>Old name → new name; unchanged lines dimmed, problems in red with the reason underneath.</summary>
    static readonly Lazy<DataTemplate> RowTemplate = new(() => (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid x:Name="Line">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="32" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <TextBlock Text="{Binding OldName}" ToolTip="{Binding OldName}" TextTrimming="CharacterEllipsis"
                           Foreground="{DynamicResource TextDimBrush}" VerticalAlignment="Top" />
                <TextBlock Grid.Column="1" Text="&#xE72A;" FontFamily="{DynamicResource IconFont}" FontSize="11"
                           HorizontalAlignment="Center" VerticalAlignment="Top" Margin="0,3,0,0" Foreground="{DynamicResource TextDimBrush}" />
                <StackPanel Grid.Column="2">
                    <TextBlock x:Name="NewName" Text="{Binding NewName}" ToolTip="{Binding NewName}" TextTrimming="CharacterEllipsis"
                               Foreground="{DynamicResource TextBrush}" />
                    <TextBlock x:Name="Why" Text="{Binding Problem}" FontSize="11.5" TextWrapping="Wrap" Margin="0,1,0,0"
                               Foreground="{DynamicResource DangerBrush}" Visibility="Collapsed" />
                </StackPanel>
            </Grid>
            <DataTemplate.Triggers>
                <DataTrigger Binding="{Binding State}" Value="Same">
                    <Setter TargetName="Line" Property="Opacity" Value="0.55" />
                    <Setter TargetName="NewName" Property="Foreground" Value="{DynamicResource TextDimBrush}" />
                </DataTrigger>
                <DataTrigger Binding="{Binding State}" Value="Problem">
                    <Setter TargetName="NewName" Property="Foreground" Value="{DynamicResource DangerBrush}" />
                    <Setter TargetName="Why" Property="Visibility" Value="Visible" />
                </DataTrigger>
            </DataTemplate.Triggers>
        </DataTemplate>
        """));

    /// <summary>Preview lines aren't selectable; they're just a padded presenter.</summary>
    static readonly Lazy<Style> RowContainer = new(() => (Style)XamlReader.Parse("""
        <Style TargetType="ListBoxItem" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Setter Property="Focusable" Value="False" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <ContentPresenter Margin="14,4" HorizontalAlignment="Stretch" />
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """));
}
