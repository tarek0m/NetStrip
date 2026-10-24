using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NetStrip.Core;
using NetStrip.Platform;

namespace NetStrip.Settings;

/// <summary>Every §10 setting except <c>version</c> and <c>floatingPosition</c>; changes apply live through the store.</summary>
public partial class SettingsWindow : Window
{
    private readonly ISettingsStore _store;
    private bool _loading;

    public SettingsWindow(ISettingsStore store, IReadOnlyList<AdapterInfo> adapters)
    {
        _store = store;
        InitializeComponent();

        _loading = true;
        FillAdapters(AdapterBox, adapters, includeAuto: true);
        FillAdapters(CapAdapterBox, adapters, includeAuto: false);
        for (var day = 1; day <= 28; day++)
            ResetDayBox.Items.Add(new ComboBoxItem { Content = day.ToString(CultureInfo.CurrentCulture), Tag = day });
        Load(store.Current);
        _loading = false;
    }

    private static void FillAdapters(ComboBox box, IReadOnlyList<AdapterInfo> adapters, bool includeAuto)
    {
        if (includeAuto) box.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = AdapterIds.Auto });
        box.Items.Add(new ComboBoxItem { Content = "All adapters", Tag = AdapterIds.All });
        foreach (var a in adapters)
            box.Items.Add(new ComboBoxItem { Content = a.Name, Tag = a.Id });
    }

    private void Load(AppSettings s)
    {
        Select(UnitsBox, s.Units.ToString());
        Select(ThemeBox, s.Theme.ToString());
        Select(MonitorsBox, s.Monitors.ToString());
        Select(CrowdedBox, s.WhenCrowded.ToString());
        IdleFadeBox.IsChecked = s.IdleFade;
        HideFullscreenBox.IsChecked = s.HideInFullscreen;
        SelectOrAdd(AdapterBox, s.Adapter);
        CapBox.Text = s.CapGb is { } gb ? gb.ToString("0.##", CultureInfo.CurrentCulture) : "";
        Select(ResetDayBox, Math.Clamp(s.CapResetDay, 1, 28));
        SelectOrAdd(CapAdapterBox, s.CapAdapter);
        StartupBox.IsChecked = StartsWithWindows();
        UpdatesBox.IsChecked = s.CheckForUpdates;
    }

    private static void Select(ComboBox box, object tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag, tag)) { box.SelectedItem = item; return; }
        }
    }

    // A saved adapter id that is no longer present stays selectable so opening the window never changes it.
    private static void SelectOrAdd(ComboBox box, string id)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if ((string)item.Tag == id) { box.SelectedItem = item; return; }
        }
        var missing = new ComboBoxItem { Content = "Unavailable adapter", Tag = id };
        box.Items.Add(missing);
        box.SelectedItem = missing;
    }

    private void Apply(Action<AppSettings> change)
    {
        if (_loading) return;
        _store.Update(change);
    }

    private static T SelectedTag<T>(ComboBox box) => (T)((ComboBoxItem)box.SelectedItem).Tag;

    private void OnUnitsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UnitsBox.SelectedItem is null) return;
        var v = Enum.Parse<SpeedUnits>(SelectedTag<string>(UnitsBox));
        Apply(s => s.Units = v);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is null) return;
        var v = Enum.Parse<ThemeSetting>(SelectedTag<string>(ThemeBox));
        Apply(s => s.Theme = v);
    }

    private void OnMonitorsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorsBox.SelectedItem is null) return;
        var v = Enum.Parse<MonitorScope>(SelectedTag<string>(MonitorsBox));
        Apply(s => s.Monitors = v);
    }

    private void OnCrowdedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CrowdedBox.SelectedItem is null) return;
        var v = Enum.Parse<CrowdedBehavior>(SelectedTag<string>(CrowdedBox));
        Apply(s => s.WhenCrowded = v);
    }

    private void OnIdleFadeClick(object sender, RoutedEventArgs e)
    {
        var v = IdleFadeBox.IsChecked == true;
        Apply(s => s.IdleFade = v);
    }

    private void OnHideFullscreenClick(object sender, RoutedEventArgs e)
    {
        var v = HideFullscreenBox.IsChecked == true;
        Apply(s => s.HideInFullscreen = v);
    }

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterBox.SelectedItem is null) return;
        var v = SelectedTag<string>(AdapterBox);
        Apply(s => s.Adapter = v);
    }

    private void OnCapAdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CapAdapterBox.SelectedItem is null) return;
        var v = SelectedTag<string>(CapAdapterBox);
        Apply(s => s.CapAdapter = v);
    }

    private void OnResetDayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResetDayBox.SelectedItem is null) return;
        var v = SelectedTag<int>(ResetDayBox);
        Apply(s => s.CapResetDay = v);
    }

    private void OnCapChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var text = CapBox.Text.Trim();
        double? value;
        if (text.Length == 0) value = null;
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var gb) && gb > 0 && gb < 1_000_000) value = gb;
        else
        {
            CapBox.SetResourceReference(BorderBrushProperty, "SystemFillColorCriticalBrush");
            return;   // keep the previous cap until the text is valid
        }
        CapBox.ClearValue(BorderBrushProperty);
        Apply(s => s.CapGb = value);
    }

    /// <summary>What Windows will actually do at sign-in for this copy: the Run key, not the saved setting.</summary>
    private static bool StartsWithWindows()
    {
        try
        {
            return Environment.ProcessPath is { } exe && StartupRegistration.IsEnabledFor(exe);
        }
        catch (Exception ex)
        {
            App.Log.Warn("Could not read the Run key", ex);
            return false;
        }
    }

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        var v = StartupBox.IsChecked == true;
        try
        {
            if (Environment.ProcessPath is not { } exe) throw new InvalidOperationException("The app's path is unknown.");
            StartupRegistration.Set(v, exe);
        }
        catch (Exception ex)
        {
            App.Log.Warn("Could not update the Run key", ex);
            StartupBox.IsChecked = StartsWithWindows();   // show what is really registered
            return;
        }
        Apply(s => s.StartWithWindows = v);   // kept in sync for reference; the Run key is the source of truth
    }

    private void OnUpdatesClick(object sender, RoutedEventArgs e)
    {
        var v = UpdatesBox.IsChecked == true;
        Apply(s => s.CheckForUpdates = v);
    }
}
