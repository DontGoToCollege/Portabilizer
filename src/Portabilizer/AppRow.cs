using System.ComponentModel;
using System.Windows.Media;
using Portabilizer.Core;

namespace Portabilizer;

/// <summary>One app in the list. Its check runs once, shared by the background pass and by selecting it.</summary>
public sealed class AppRow(InstalledApp app) : INotifyPropertyChanged
{
    readonly object _gate = new();
    Task<Plan>? _plan;

    public InstalledApp App { get; } = app;
    public string Name => App.Name;
    public string Detail => App.Detail;
    public Level? Verdict { get; private set; }
    public Brush Dot => Verdict is { } v ? MainWindow.BrushFor(v) : Brushes.Gray;
    public string VerdictText => Verdict is { } v ? MainWindow.TextFor(v) : "Checking...";

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name; // what screen readers announce for the list item

    public Task<Plan> GetPlan()
    {
        lock (_gate) return _plan ??= Task.Run(Check);
    }

    public void Show(Level verdict)
    {
        Verdict = verdict;
        PropertyChanged?.Invoke(this, new(nameof(Verdict)));
        PropertyChanged?.Invoke(this, new(nameof(Dot)));
        PropertyChanged?.Invoke(this, new(nameof(VerdictText)));
    }

    Plan Check()
    {
        try { return Inspector.Inspect(App); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var plan = new Plan { App = App };
            plan.Findings.Add(new(Level.Blocked, $"Couldn't check this app: {e.Message}"));
            return plan;
        }
    }
}
