using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace OnuradaMapper.Views;

public partial class JsonEditWindow : Window
{
    public JsonObject? Result { get; private set; }

    public JsonEditWindow(string title, string json)
    {
        InitializeComponent();
        Title = title;
        Box.Text = json;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = JsonNode.Parse(Box.Text) as JsonObject ?? throw new JsonException("{ ... }");
            DialogResult = true;
        }
        catch (JsonException ex) { Err.Text = ex.Message; }
    }
}
