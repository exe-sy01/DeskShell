using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace DeskShell.Services;

public sealed class Note
{
    public string Text { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
}

/// <summary>Quick notes from the notch, newest first, in %APPDATA%\DeskShell\notes.json.</summary>
public static class NotesStore
{
    static readonly string FilePath = Path.Combine(Settings.Folder, "notes.json");
    static ObservableCollection<Note>? _items;

    public static ObservableCollection<Note> Items
    {
        get
        {
            if (_items != null) return _items;
            _items = new ObservableCollection<Note>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var note in JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(FilePath)) ?? new())
                        _items.Add(note);
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't read notes", ex);
            }
            return _items;
        }
    }

    public static void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Items.Insert(0, new Note { Text = text.Trim() });
        Save();
    }

    public static void Remove(Note note)
    {
        Items.Remove(note);
        Save();
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save notes", ex);
        }
    }
}
