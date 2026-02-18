using System.Configuration;
using System.Data;
using System.Windows;
using static ForegroundTracker.EventLog;

namespace ForegroundTracker;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void App_OnActivated(object? sender, EventArgs e) { if (ForegroundTracker.MainWindow.EnableAppActivated) Log("App.OnActivated"); }
    private void App_OnDeactivated(object? sender, EventArgs e) { if (ForegroundTracker.MainWindow.EnableAppActivated) Log("App.OnDeactivated"); }
}