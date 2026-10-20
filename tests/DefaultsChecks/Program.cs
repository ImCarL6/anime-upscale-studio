using Anime4KEncoder;
using System.Windows.Controls;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A staged runtime supplies models/tools without starting any encoder.
        Environment.CurrentDirectory = args[0];
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        var engine = ((ComboBox)window.FindName("EngineCombo")).SelectedItem;
        var profile = ((ComboBox)window.FindName("ProfileCombo")).SelectedItem;
        var cq = ((TextBox)window.FindName("CqText")).Text;
        if ((string?)engine?.GetType().GetProperty("Code")?.GetValue(engine) != "animejanai" ||
            (string?)profile?.GetType().GetProperty("Code")?.GetValue(profile) != "V3.1-Balanced-Sharp1" || cq != "18" || ((ComboBox)window.FindName("ResourceModeCombo")).SelectedIndex != 1)
            throw new Exception("Unexpected startup defaults.");
        Console.WriteLine("STARTUP_DEFAULTS=PASS: AnimeJaNai, V3.1 Balanced Sharp1, CQ 18.");
        window.Close();
        app.Shutdown();
    }
}
