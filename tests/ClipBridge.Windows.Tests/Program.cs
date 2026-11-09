using System.Reflection;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Inspect the actual application entry point, not a test-only surrogate thread.
        var assembly = Assembly.Load("ClipBridge.Windows");
        var entry = assembly.EntryPoint ?? throw new Exception("Application entry point missing.");
        if (entry.GetCustomAttribute<STAThreadAttribute>() is null)
            throw new Exception("Windows entry point must use STA for file dialogs and Clipboard APIs.");
        Console.WriteLine("PASS built Windows entry point uses STA for file dialogs and clipboard");
    }
}
