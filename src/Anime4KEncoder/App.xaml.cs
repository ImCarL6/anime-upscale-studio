using System.Configuration;
using System.Data;
using System.Windows;
using Velopack;

namespace Anime4KEncoder;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var instance = new Mutex(true, @"Local\ImCarL6.AnimeUpscaleStudio", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("O Anime Upscale Studio já está aberto.", "Anime Upscale Studio");
            return;
        }
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        finally { instance.ReleaseMutex(); }
    }
}
