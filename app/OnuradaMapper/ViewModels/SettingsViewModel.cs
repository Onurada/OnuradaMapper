using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OnuradaMapper.Services;

namespace OnuradaMapper.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public event Action? Saved;

    [ObservableProperty] private string _workspace = "";
    [ObservableProperty] private string _gameDir = "";
    [ObservableProperty] private string _pythonPath = "";
    [ObservableProperty] private string _mapperName = "";
    [ObservableProperty] private Choice? _language;
    [ObservableProperty] private Choice? _device;
    [ObservableProperty] private Choice? _whisper;
    [ObservableProperty] private bool _installAfterBuild;
    [ObservableProperty] private Choice? _theme;
    public List<Choice> Themes => [new("system", Loc.T("ThemeSystem")), new("light", Loc.T("ThemeLight")), new("dark", Loc.T("ThemeDark"))];
    [ObservableProperty] private string _message = "";

    public List<Choice> Languages { get; } = [new("en", "English"), new("tr", "Türkçe")];
    public List<Choice> Devices { get; } = [new("auto", "Auto"), new("cuda", "GPU (CUDA)"), new("cpu", "CPU")];
    public List<Choice> WhisperModels { get; } = [new("large-v3", "large-v3 (best)"), new("medium", "medium"), new("small", "small (fast, CPU)")];

    public SettingsViewModel() => Load();

    bool _loading;

    void Load()
    {
        _loading = true;
        var s = AppEnv.Settings;
        Workspace = s.Workspace; GameDir = s.GameDir; PythonPath = s.PythonPath; MapperName = s.MapperName;
        Language = Languages.FirstOrDefault(c => c.Value == s.Language) ?? Languages[0];
        Device = Devices.FirstOrDefault(c => c.Value == s.Device) ?? Devices[0];
        Whisper = WhisperModels.FirstOrDefault(c => c.Value == s.WhisperModel) ?? WhisperModels[0];
        InstallAfterBuild = s.InstallAfterBuild;
        Theme = Themes.FirstOrDefault(c => c.Value == s.Theme) ?? Themes[0];
        _loading = false;
    }

    partial void OnLanguageChanged(Choice? value) { if (value != null && !_loading) Loc.Apply(value.Value); }
    partial void OnThemeChanged(Choice? value) { if (value != null && !_loading) App.ApplyTheme(value.Value); }

    [RelayCommand] void BrowseWorkspace() { var d = new OpenFolderDialog(); if (d.ShowDialog() == true) Workspace = d.FolderName; }
    [RelayCommand] void BrowseGame() { var d = new OpenFolderDialog(); if (d.ShowDialog() == true) GameDir = d.FolderName; }
    [RelayCommand] void BrowsePython() { var d = new OpenFileDialog { Filter = "python.exe|python.exe" }; if (d.ShowDialog() == true) PythonPath = d.FileName; }

    [RelayCommand]
    void Save()
    {
        var name = MapperName.Trim();
        if (name.Length == 0 || name.Any(ch => ch > 127 || "\\/:*?\"<>|()".Contains(ch))) { Message = Loc.T("ErrMapperName"); return; }
        var s = AppEnv.Settings;
        s.Workspace = Workspace.Trim(); s.GameDir = GameDir.Trim(); s.PythonPath = PythonPath.Trim(); s.MapperName = name;
        s.Language = Language?.Value ?? "en"; s.Device = Device?.Value ?? "auto"; s.WhisperModel = Whisper?.Value ?? "large-v3";
        s.InstallAfterBuild = InstallAfterBuild;
        s.Theme = Theme?.Value ?? "system";
        AppEnv.Save();
        Message = Loc.T("SettingsSaved");
        Saved?.Invoke();
    }
}
