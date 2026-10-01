using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Slate;

/// <summary>Command history with shell-style ↑/↓ navigation, persisted to disk.</summary>
internal sealed class History
{
    private readonly string _path;
    private readonly List<string> _items;
    private int _index = -1; // -1 = not navigating
    private string _draft = string.Empty;

    public int MaxSize { get; set; }

    public History(string path, int maxSize)
    {
        _path = path;
        MaxSize = Math.Max(1, maxSize);
        _items = File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0).ToList()
            : new List<string>();
    }

    public void Add(string command)
    {
        _items.Remove(command);
        _items.Add(command);
        if (_items.Count > MaxSize) _items.RemoveRange(0, _items.Count - MaxSize);
        ResetNavigation();
        Save();
    }

    /// <summary>Older entry, or null if there is none.</summary>
    public string? Previous(string current)
    {
        if (_items.Count == 0) return null;
        if (_index == -1)
        {
            _draft = current;
            _index = _items.Count;
        }
        if (_index > 0) _index--;
        return _items[_index];
    }

    /// <summary>Newer entry; past the newest returns the original draft. Null if not navigating.</summary>
    public string? Next()
    {
        if (_index == -1) return null;
        _index++;
        if (_index < _items.Count) return _items[_index];
        _index = -1;
        return _draft;
    }

    public void ResetNavigation() => _index = -1;

    public void Clear()
    {
        _items.Clear();
        ResetNavigation();
        Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllLines(_path, _items);
        }
        catch (IOException ex)
        {
            Log.Error("Saving history", ex);
        }
    }
}
