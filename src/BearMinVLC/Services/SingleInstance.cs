using System.IO.Pipes;
using Avalonia.Threading;

namespace BearMinVLC.Services;

/// <summary>Keeps one player window per user; later launches forward their file path over a named pipe.</summary>
static class SingleInstance
{
    static readonly string Name = $"BearMinVLC-{Environment.UserName}";
    static Mutex? _mutex;

    public static bool TryBecomePrimary(string[] args)
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{Name}", out bool created);
        if (created)
            return true;

        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.WriteLine(args.FirstOrDefault() ?? "");
        }
        catch (Exception)
        {
            // The first instance is starting up or shutting down; nothing useful to do.
        }
        return false;
    }

    public static void Listen(Action<string> onOpen)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var line = reader.ReadLine() ?? "";
                    Dispatcher.UIThread.Post(() => onOpen(line));
                }
                catch (Exception)
                {
                    Thread.Sleep(250);
                }
            }
        })
        { IsBackground = true, Name = "SingleInstance" };
        thread.Start();
    }
}
