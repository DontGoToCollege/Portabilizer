using System.Text;
using System.Windows;
using Portabilizer.Core;

namespace Portabilizer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var root = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (File.Exists(Path.Combine(root, Manifest.FileName)))
        {
            Shutdown(Launcher.Run(root, e.Args));
            return;
        }

        if (e.Args.Length > 0)
        {
            Shutdown(CommandLine(e.Args));
            return;
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;
        new MainWindow().Show();
    }

    // Used for smoke checks:
    //   --report <file> [name filter]      writes each app's verdict and what would be captured
    //   --capture <app name> <out folder>  captures one app
    static int CommandLine(string[] args)
    {
        var apps = InstalledApps.Scan();
        switch (args[0])
        {
            case "--report" when args.Length >= 2:
                var text = new StringBuilder();
                foreach (var app in apps.Where(a => args.Length < 3 || a.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase)))
                {
                    var plan = Inspector.Inspect(app);
                    text.AppendLine($"{app.Name} [{plan.Verdict}] {app.InstallPath} ({Capturer.Size(plan.AppBytes)}, data {Capturer.Size(plan.DataBytes)})");
                    text.AppendLine($"  main: {plan.Exes.FirstOrDefault()}");
                    foreach (var f in plan.Folders) text.AppendLine($"  folder: {f.Target}");
                    foreach (var k in plan.Keys) text.AppendLine($"  key: {k}");
                    foreach (var f in plan.Findings) text.AppendLine($"  {f.Level}: {f.Text}");
                }
                File.WriteAllText(args[1], text.ToString());
                return 0;

            case "--capture" when args.Length == 3:
                var target = apps.FirstOrDefault(a => a.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                if (target is null) return 2;
                var capture = Inspector.Inspect(target);
                if (capture.Verdict == Level.Blocked) return 3;
                Capturer.Run(capture, capture.Exes[0], includeData: true, args[2], new Progress<(double, string)>());
                return 0;

            default:
                return 2;
        }
    }
}
