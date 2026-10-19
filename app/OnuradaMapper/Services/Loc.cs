using System.Windows;

namespace OnuradaMapper.Services;

/// <summary>UI strings live in Lang/*.xaml dictionaries; switching language swaps the merged dictionary.</summary>
public static class Loc
{
    static ResourceDictionary? _current;
    public static event Action? Changed;

    public static void Apply(string lang)
    {
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Lang/{(lang == "tr" ? "tr" : "en")}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
        Changed?.Invoke();
    }

    public static string T(string key) =>
        Application.Current?.Dispatcher.Invoke(() => Application.Current.TryFindResource(key) as string) ?? key;
}
