namespace DevKitRelay;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        ApplicationConfiguration.Initialize();

        try
        {
            var options = CommandLineOptions.Parse(args);

            switch (options.Mode)
            {
                case AppMode.ListWindows:
                    foreach (var window in WindowCatalog.GetVisibleWindows())
                    {
                        Console.WriteLine($"{window.Handle.ToInt64(),12}  {window.Title}");
                    }
                    break;

                case AppMode.Server:
                    await RelayServer.RunAsync(options, CancellationToken.None);
                    break;

                case AppMode.Client:
                    Application.Run(new RelayClientForm(options.ServerUri, options.ClientDurationSeconds));
                    break;

                default:
                    CommandLineOptions.PrintUsage();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }
    }
}
