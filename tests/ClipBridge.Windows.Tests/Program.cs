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
        if (args is not ["--entry-only"])
        {
            // Exercise the real import handler. A tester cancels/selects in the native dialog.
            ApplicationConfiguration.Initialize();
            Console.WriteLine("Initializing real import window");
            var type = assembly.GetType("BridgeWindow")!;
            using var form = (Form)Activator.CreateInstance(type)!;
            form.Shown += (_, _) => form.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                Console.WriteLine("Entering real import handler");
                try { type.GetMethod("Import", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null); }
                finally { form.Close(); }
            }));
            Console.WriteLine("Starting Windows message loop");
            Application.Run(form);
            Console.WriteLine("PASS native import handler completed");
        }
    }
}
