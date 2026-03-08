using Elochka.App.Application;

namespace Elochka.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new ElochkaApplicationContext());
    }
}
