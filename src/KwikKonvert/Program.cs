using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using KwikKonvert.Platform;

namespace KwikKonvert;

/// <summary>
/// Entry point. KwikKonvert runs as a single instance: Explorer starts one process per selected file, so every
/// later copy just hands its arguments to the first one over a named pipe (current user only) and exits.
/// </summary>
internal static class Program
{
    private static readonly string InstanceId = $"KwikKonvert-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
    private static string MutexName => @"Local\" + InstanceId;
    private static string PipeName => InstanceId;

    [STAThread]
    private static int Main(string[] args)
    {
        // Uninstall helper: works without starting the UI.
        if (args.Length > 0 && args[0] == "--unregister")
        {
            ExplorerIntegration.RemoveAll();
            StartupService.RemoveLegacyAutostart();
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            if (TrySendToFirstInstance(args)) return 0;
            // The first instance is shutting down or stuck: carry on as a new one rather than losing the request.
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowCrash(e.Exception);

        using var host = new AppHost();
        using var pipeCts = new CancellationTokenSource();
        if (isFirst) _ = Task.Run(() => ListenAsync(host, pipeCts.Token));

        host.Start(args);
        Application.Run(host.Context);

        pipeCts.Cancel();
        return 0;
    }

    private static void ShowCrash(Exception ex)
    {
        MessageBox.Show("Something went wrong:\n\n" + ex.Message, "KwikKonvert", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ------------------------------------------------------------------ single instance

    private static bool TrySendToFirstInstance(string[] args)
    {
        // Let the running copy bring its window to the front.
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

        // Paths must be absolute: the other process has a different current directory.
        var payload = args.Select(a => !a.StartsWith("--", StringComparison.Ordinal) && (File.Exists(a) || Directory.Exists(a)) ? Path.GetFullPath(a) : a).ToArray();

        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(200);
                using var writer = new BinaryWriter(client, Encoding.UTF8);
                writer.Write(payload.Length);
                foreach (var a in payload) writer.Write(a);
                writer.Flush();
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { Thread.Sleep(100); }
        }
        return false;
    }

    private static async Task ListenAsync(AppHost host, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                string[] args;
                using (var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true))
                {
                    var count = reader.ReadInt32();
                    if (count is < 0 or > 10_000) continue;
                    args = new string[count];
                    for (var i = 0; i < count; i++) args[i] = reader.ReadString();
                }
                host.Post(() => host.HandleArguments(args, fromOtherInstance: true));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Debug.WriteLine("Instance pipe: " + ex.Message);
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
