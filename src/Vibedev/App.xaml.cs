using System.Configuration;
using System.Data;
using System.Windows;

namespace Vibedev;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    internal bool SuppressStartupWindow { get; init; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!SuppressStartupWindow) new MainWindow().Show();
    }
}

