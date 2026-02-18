namespace ForegroundTracker;

public class Program
{
    [STAThread]
    public static void Main()
    {
        Thread.Sleep(2000);
        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
    }
}
