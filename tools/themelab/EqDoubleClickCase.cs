using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioFool;
using AudioFool.Core.Equalizer;
using AudioFool.Core.Settings;
using AudioFool.Core.Theme;
using AudioFool.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ThemeLab;

/// <summary>
/// --window eqdouble [--theme dark] [--eqpreset "IER-M9 ChatGPT"] (session 80): a
/// double-click on the status bar's EQ chip toggles the equalizer, through the real
/// MainWindow and MainViewModel. The chip's presses are raised
/// PreviewMouseLeftButtonDown events; a single click is the chip's Click raised, which
/// builds the menu through the window's own handler (its popup never opened, as in
/// --window equalizer); the second press is raised on that menu, as it lands while
/// the open menu has the mouse. MouseEventArgs.GetPosition reads the mouse device,
/// not the event, so the press's position is set on Mouse.PrimaryDevice by reflection
/// (its input source and last position, in the window's client pixels) and put back
/// after each press; the real pointer is never moved. Settings save to a scratch file
/// beside --out (the settings object's path is set by reflection); the real
/// settings.json is hashed before and after. Peace's presets are read, never written.
/// Checks: a double-click turns off and back on (menu closed, dot, panel, settings,
/// file), too late and off the chip do nothing, never used does nothing, a preset
/// gone says so and stays off, Off from the menu keeps the last preset across a
/// reload, a single click's menu as before, the status bar's pixels after a toggle
/// = the state set directly. "ALL OK".
/// </summary>
internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private static int RunEqDouble(MainWindow main, MainViewModel vm, string outPath, double w, double h, double scale, string presetName)
    {
        var stem = System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(outPath), null);
        var allOk = true;
        void Check(bool ok, string what)
        {
            allOk &= ok;
            Console.WriteLine($"  {(ok ? "OK" : "WRONG")}: {what}");
        }

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var chip = (ButtonBase)main.FindName("EqualizerChip");
        var statusBar = (Grid)main.FindName("StatusBar");
        var eqDot = (Ellipse)main.FindName("EqualizerDot");
        var panel = (FrameworkElement)main.FindName("EqualizerPanel");
        var dblTime = (int)GetDoubleClickTime();
        Pump();
        Console.WriteLine($"theme: {ThemeService.Current}; double-click time {dblTime} ms; header shown {vm.ShowsAlbumHeader}/{vm.ShowsPlaylistHeader}");

        Rect Box(FrameworkElement e) => e.TransformToAncestor(main).TransformBounds(new Rect(e.RenderSize));
        Color Token(string key) => ((SolidColorBrush)main.FindResource(key)).Color;

        // ---------------------------------------------------------- settings to scratch
        var realSettings = AppSettings.SettingsPath;
        string? Hash(string p) => File.Exists(p) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))[..16] : null;
        var realBefore = Hash(realSettings);
        var realTimeBefore = File.Exists(realSettings) ? File.GetLastWriteTimeUtc(realSettings) : DateTime.MinValue;
        var settingsField = typeof(MainViewModel).GetField("_settings", flags)!;
        var pathField = typeof(AppSettings).GetField("_path", flags)
            ?? throw new InvalidOperationException("AppSettings._path not found: not running, it could save the real settings");
        var settings = (AppSettings)settingsField.GetValue(vm)!;
        var scratchDir = $"{stem}-settings";
        Directory.CreateDirectory(scratchDir);
        var scratch = System.IO.Path.Combine(scratchDir, "settings.json");
        if (File.Exists(scratch))
            File.Delete(scratch);
        pathField.SetValue(settings, scratch);
        Console.WriteLine($"settings save to {scratch} (real {realSettings}, hash {realBefore ?? "none"})");
        settings.EqualizerPreset = settings.EqualizerFolder = settings.EqualizerLastPreset = settings.EqualizerLastFolder = null;

        AppSettings OnDisk() => AppSettings.Load(scratch, DateTime.UtcNow);
        string S(AppSettings s) => $"EqualizerPreset {s.EqualizerPreset ?? "null"}/{s.EqualizerFolder ?? "null"}, Last {s.EqualizerLastPreset ?? "null"}/{s.EqualizerLastFolder ?? "null"}";

        // ---------------------------------------------------------- the mouse's position
        // GetPosition asks the mouse device, which answers from its input source and
        // last client position: both set here for the length of one press.
        var mouse = Mouse.PrimaryDevice;
        var srcField = typeof(MouseDevice).GetField("_inputSource", flags)
            ?? throw new InvalidOperationException("MouseDevice._inputSource not found");
        var posField = typeof(MouseDevice).GetField("_lastPosition", flags)
            ?? throw new InvalidOperationException("MouseDevice._lastPosition not found");
        Console.WriteLine($"mouse device {mouse.GetType().Name}: _inputSource {srcField.FieldType.Name}, _lastPosition {posField.FieldType.Name}");
        var hwnd = (HwndSource)PresentationSource.FromVisual(main)!;
        object? MakeSource()
        {
            if (srcField.FieldType.IsAssignableFrom(typeof(HwndSource)))
                return hwnd;
            return Activator.CreateInstance(srcField.FieldType, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, [hwnd], null);
        }
        var fakeSource = MakeSource();

        T At<T>(Point inWindow, Func<T> act)
        {
            var savedSrc = srcField.GetValue(mouse);
            var savedPos = posField.GetValue(mouse);
            try
            {
                srcField.SetValue(mouse, fakeSource);
                posField.SetValue(mouse, hwnd.CompositionTarget.TransformToDevice.Transform(inWindow));
                return act();
            }
            finally
            {
                // Mouse.Synchronize may have run on the faked position and left the chip
                // "under the mouse" (its hover look): moved off the window, then put back.
                posField.SetValue(mouse, new Point(-5000, -5000));
                Mouse.Synchronize();
                Pump();
                srcField.SetValue(mouse, savedSrc);
                posField.SetValue(mouse, savedPos);
            }
        }

        var chipBox = Box(chip);
        var onChip = new Point(chipBox.Left + chipBox.Width / 2, chipBox.Top + chipBox.Height / 2);
        var overMenu = new Point(chipBox.Left + 20, chipBox.Top - 40);  // where the menu's items sit (it opens above the chip)

        MouseButtonEventArgs Press(UIElement target, int clickCount = 1)
        {
            var e = new MouseButtonEventArgs(mouse, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                Source = target,
            };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(e, clickCount);
            target.RaiseEvent(e);
            Pump();
            return e;
        }

        {
            var probe = At(onChip, () => mouse.GetPosition(chip));
            var probe2 = At(overMenu, () => mouse.GetPosition(chip));
            Check(Math.Abs(probe.X - chipBox.Width / 2) <= 1 && Math.Abs(probe.Y - chipBox.Height / 2) <= 1,
                $"faked position on the chip reads ({probe.X:0.##},{probe.Y:0.##}) relative to it (chip {chipBox.Width:0.##}x{chipBox.Height:0.##})");
            Check(Math.Abs(probe2.X - 20) < 0.5 && Math.Abs(probe2.Y + 40) < 0.5,
                $"faked position over the menu reads ({probe2.X:0.##},{probe2.Y:0.##}) relative to the chip: off it");
            Console.WriteLine($"  real mouse device after: GetPosition(chip) ({mouse.GetPosition(chip).X:0},{mouse.GetPosition(chip).Y:0}), chip IsMouseOver {chip.IsMouseOver}");
        }

        // ---------------------------------------------------------- the menu, as a click opens it
        var menuPresets = vm.LoadEqualizerMenu();
        var chosen = menuPresets.Peace.FirstOrDefault(p => p.Name == presetName)
            ?? throw new InvalidOperationException($"{presetName} isn't among Peace's presets offered");
        Console.WriteLine($"preset: {chosen.Name} ({chosen.SourcePath})");

        List<string> Expected()
        {
            var expected = new List<string> { "Off", "----" };
            expected.AddRange(menuPresets.Peace.Select(p => p.Name));
            if (menuPresets.Own.Count > 0)
            {
                expected.Add("----");
                expected.AddRange(menuPresets.Own.Select(p => p.Name));
            }
            expected.Add("----");
            expected.Add("Open EQ Folder…");
            if (MainViewModel.PeaceExecutable is not null)
                expected.Add("Open Peace…");
            return expected;
        }

        List<string> Entries(ContextMenu m) => m.Items.Cast<object>().Select(i => i switch
        {
            MenuItem mi => AccessText(mi.Header),
            Separator => "----",
            _ => i.ToString() ?? "",
        }).ToList();

        // A click on the chip: its own press first (timed by the window), then Click.
        ContextMenu Click(out MouseButtonEventArgs press)
        {
            press = Press(chip);
            var menu = OpenEqualizerMenuByClick(chip);
            Pump();
            return menu;
        }

        MouseButtonEventArgs MenuPress(ContextMenu menu, Point where) => At(where, () => Press(menu));

        void Close(ContextMenu menu)
        {
            menu.IsOpen = false;
            Pump();
        }

        string State() => $"on {vm.IsEqualizerOn}, dot {Hex(((SolidColorBrush)eqDot.Fill).Color)}, panel {panel.Visibility}, name '{vm.EqualizerAutomationName}', readout '{vm.OutputDescription}'";
        bool DotIs(string token) => ((SolidColorBrush)eqDot.Fill).Color == Token(token);
        void LetDoubleClickLapse() => Settle(dblTime + 150);

        // ---------------------------------------------------------- baselines, set directly
        var raiseChanged = typeof(MainViewModel).GetMethod("OnEqualizerChanged", flags)!;
        void SetEq(EqPreset? preset, string? folder)
        {
            typeof(MainViewModel).GetField("_equalizerPreset", flags)!.SetValue(vm, preset);
            typeof(MainViewModel).GetField("_equalizerFolder", flags)!.SetValue(vm, folder);
            raiseChanged.Invoke(vm, null);
            main.UpdateLayout();
            Pump();
        }
        var barBox = Box(statusBar);
        var barRect = new Int32Rect((int)Math.Floor(barBox.Left * scale), (int)Math.Floor(barBox.Top * scale),
            (int)Math.Ceiling(barBox.Width * scale), (int)Math.Ceiling(barBox.Height * scale));
        string Where(EqShot a, EqShot b)
        {
            int l = int.MaxValue, t = int.MaxValue, r = -1, bt = -1;
            for (var y = barRect.Y; y < barRect.Y + barRect.Height; y++)
                for (var x = barRect.X; x < barRect.X + barRect.Width; x++)
                    if (a.At(x, y) != b.At(x, y)) { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); bt = Math.Max(bt, y); }
            return r < 0 ? "none" : $"x {l}-{r}, y {t}-{bt}";
        }
        EqShot Shot(string name)
        {
            Console.WriteLine($"  [{name}] chip IsMouseOver {chip.IsMouseOver}, readout '{vm.OutputDescription}'");
            var path = $"{stem}-{name}.png";
            Save(main, path, w, h, scale);
            return LoadShot(path);
        }
        // The readout as ChooseEqualizer leaves it (ThemeLab's engine has no output: "not started").
        var engineObj = (AudioFool.Core.Playback.AudioEngine)typeof(MainViewModel).GetField("_engine", flags)!.GetValue(vm)!;
        Console.WriteLine($"readout before: '{vm.OutputDescription}', engine's: '{engineObj.OutputDescription}'");
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.OutputDescription))!.SetValue(vm, engineObj.OutputDescription);
        SetEq(chosen, AppSettings.EqualizerInPeace);
        var baseOn = Shot("base-on");
        SetEq(null, null);
        var baseOff = Shot("base-off");
        Console.WriteLine($"status bar region (px) {barRect.X},{barRect.Y} {barRect.Width}x{barRect.Height}; chip IsMouseOver {chip.IsMouseOver}");

        // ---------------------------------------------------------- 8. a single click
        Console.WriteLine("8. a single click opens the menu as before:");
        {
            var menu = Click(out var press);
            var entries = Entries(menu);
            Console.WriteLine($"  items: {string.Join(" | ", entries)}");
            var popup = (Popup)typeof(ContextMenu).GetField("_parentPopup", flags)!.GetValue(menu)!;
            Check(!press.Handled && menu.IsOpen && !popup.IsOpen && PresentationSource.FromVisual(menu) is null,
                $"chip press not handled ({press.Handled}); menu built, IsOpen {menu.IsOpen}, no popup window (popup IsOpen {popup.IsOpen})");
            Check(menu.Placement == PlacementMode.Top && ReferenceEquals(menu.PlacementTarget, chip), $"placement {menu.Placement} on the chip");
            Check(entries.SequenceEqual(Expected()), "items as --window equalizer expects: Off, line, Peace's, [line, own], line, Open EQ Folder…, Open Peace…");
            var ticked = menu.Items.OfType<MenuItem>().Where(m => m.IsChecked).Select(m => AccessText(m.Header)).ToList();
            Check(ticked.SequenceEqual(["Off"]), $"checked: {string.Join(", ", ticked)}");
            Check(!vm.IsEqualizerOn, $"nothing toggled: {State()}");
            Close(menu);
        }
        LetDoubleClickLapse();

        // ---------------------------------------------------------- 5. never used
        Console.WriteLine("5. off and never used:");
        {
            Check(!vm.CanToggleEqualizer && settings.LastEqualizer is null, $"CanToggleEqualizer {vm.CanToggleEqualizer}, LastEqualizer {(settings.LastEqualizer is null ? "null" : "set")}");
            var menu = Click(out _);
            var second = MenuPress(menu, onChip);
            Check(!second.Handled && menu.IsOpen && !vm.IsEqualizerOn && !File.Exists(scratch),
                $"double-click: handled {second.Handled}, menu IsOpen {menu.IsOpen}, {State()}, settings file written {File.Exists(scratch)}");
            Close(menu);
            var chipDouble = At(onChip, () => { Press(chip); return Press(chip, 2); });
            Check(!chipDouble.Handled && !vm.IsEqualizerOn, $"a ClickCount 2 press on the chip: handled {chipDouble.Handled}, on {vm.IsEqualizerOn}");
        }
        LetDoubleClickLapse();

        // Turned on from the menu, as a user would.
        {
            var menu = Click(out _);
            var item = menu.Items.OfType<MenuItem>().First(m => AccessText(m.Header) == chosen.Name);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            Close(menu);
            Console.WriteLine($"  chosen from the menu: {State()}; {S(settings)}");
        }
        LetDoubleClickLapse();

        // ---------------------------------------------------------- 1. on -> off
        Console.WriteLine($"1. on ({chosen.Name}), double-click:");
        {
            var menu = Click(out var first);
            var second = MenuPress(menu, onChip);
            var disk = OnDisk();
            Check(!first.Handled && second.Handled && !menu.IsOpen, $"first press handled {first.Handled}; second (on the menu, over the chip) handled {second.Handled}; menu IsOpen {menu.IsOpen}");
            Check(!vm.IsEqualizerOn && DotIs("color.status.eqOff") && !vm.OutputDescription.Contains("· EQ") && panel.Visibility == Visibility.Collapsed && vm.EqualizerAutomationName == "Equalizer: off",
                $"off: {State()}");
            Check(settings.EqualizerPreset is null && settings.EqualizerLastPreset == chosen.Name && settings.EqualizerLastFolder == AppSettings.EqualizerInPeace,
                $"settings: {S(settings)}");
            Check(disk.EqualizerPreset is null && disk.EqualizerLastPreset == chosen.Name, $"file: {S(disk)}");
            Console.WriteLine($"  chip IsMouseOver {chip.IsMouseOver}");
            var shot = Shot("toggled-off");
            Check(shot.Diff(baseOff, barRect) == 0, $"status bar pixels after the toggle = off set directly ({shot.Diff(baseOff, barRect)} differ, {Where(shot, baseOff)})");
            Check(shot.Diff(baseOn, barRect) > 0, $"(the comparison sees the dot: against on, {shot.Diff(baseOn, barRect)} differ at {Where(shot, baseOn)})");
        }
        LetDoubleClickLapse();

        // ---------------------------------------------------------- 2. off -> last preset
        Console.WriteLine("2. off with a last preset, double-click:");
        {
            var menu = Click(out _);
            var second = MenuPress(menu, onChip);
            var disk = OnDisk();
            Check(second.Handled && !menu.IsOpen, $"handled {second.Handled}; menu IsOpen {menu.IsOpen}");
            var inUse = (EqPreset?)typeof(MainViewModel).GetField("_equalizerPreset", flags)!.GetValue(vm);
            Check(vm.IsEqualizerOn && inUse?.Name == chosen.Name && DotIs("color.status.eqOn") && panel.Visibility == Visibility.Visible
                  && vm.EqualizerAutomationName == $"Equalizer: {chosen.Name}" && vm.IsEqualizerInUse(chosen, AppSettings.EqualizerInPeace),
                $"on: {State()}; read afresh {(inUse is not null && !ReferenceEquals(inUse, chosen))}");
            Check(settings.EqualizerPreset == chosen.Name && settings.EqualizerFolder == AppSettings.EqualizerInPeace && settings.EqualizerLastPreset == chosen.Name,
                $"settings: {S(settings)}");
            Check(disk.EqualizerPreset == chosen.Name && disk.EqualizerFolder == AppSettings.EqualizerInPeace, $"file: {S(disk)}");
            var shot = Shot("toggled-on");
            Check(shot.Diff(baseOn, barRect) == 0, $"status bar pixels after the toggle = on set directly ({shot.Diff(baseOn, barRect)} differ, {Where(shot, baseOn)})");
        }
        LetDoubleClickLapse();

        // ---------------------------------------------------------- 3. too late
        Console.WriteLine("3. second press too late:");
        {
            var menu = Click(out _);
            LetDoubleClickLapse();
            var second = MenuPress(menu, onChip);
            Check(!second.Handled && menu.IsOpen && vm.IsEqualizerOn, $"after {dblTime + 150} ms: handled {second.Handled}, menu IsOpen {menu.IsOpen}, on {vm.IsEqualizerOn}");
            Close(menu);
        }
        LetDoubleClickLapse();

        // ---------------------------------------------------------- 4. off the chip
        Console.WriteLine("4. second press on the menu, not over the chip:");
        {
            var menu = Click(out _);
            var where = At(overMenu, () => mouse.GetPosition(chip));
            var second = MenuPress(menu, overMenu);
            Check(!second.Handled && menu.IsOpen && vm.IsEqualizerOn, $"at ({where.X:0},{where.Y:0}) from the chip: handled {second.Handled}, menu IsOpen {menu.IsOpen}, on {vm.IsEqualizerOn}");
            Close(menu);
        }
        LetDoubleClickLapse();

        // A double-click on the chip with no menu open (ClickCount 2), both ways.
        Console.WriteLine("extra: ClickCount 2 on the chip, no menu:");
        {
            var off = At(onChip, () => { Press(chip); return Press(chip, 2); });
            var wasOff = !vm.IsEqualizerOn;
            LetDoubleClickLapse();
            var on = At(onChip, () => { Press(chip); return Press(chip, 2); });
            Check(off.Handled && wasOff && on.Handled && vm.IsEqualizerOn, $"off (handled {off.Handled}, was off {wasOff}), then on (handled {on.Handled}, on {vm.IsEqualizerOn})");
            LetDoubleClickLapse();
            var far = At(overMenu, () => { Press(chip); return Press(chip, 2); });
            Check(!far.Handled && vm.IsEqualizerOn, $"ClickCount 2 reported off the chip: handled {far.Handled}, on {vm.IsEqualizerOn}");
        }
        LetDoubleClickLapse();

        // ---------------------------------------------------------- 7. Off from the menu keeps the last
        Console.WriteLine("7. Off from the menu, then a restart:");
        {
            var menu = Click(out _);
            var off = menu.Items.OfType<MenuItem>().First(m => AccessText(m.Header) == "Off");
            off.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, off));
            Close(menu);
            var disk = OnDisk();
            Check(!vm.IsEqualizerOn && settings.EqualizerPreset is null && settings.EqualizerLastPreset == chosen.Name,
                $"off from the menu: {State()}; settings {S(settings)}");
            Check(disk.EqualizerPreset is null && disk.EqualizerLastPreset == chosen.Name && disk.LastEqualizer == (chosen.Name, AppSettings.EqualizerInPeace),
                $"reloaded from {System.IO.Path.GetFileName(scratch)}: {S(disk)}; LastEqualizer {disk.LastEqualizer}");
            Console.WriteLine($"  on disk: {string.Join(" ", File.ReadAllLines(scratch).Where(l => l.Contains("Equalizer")).Select(l => l.Trim()))}");

            // The reloaded settings in the view model, as a restart would give it.
            settingsField.SetValue(vm, disk);
            settings = disk;
            LetDoubleClickLapse();
            var menu2 = Click(out _);
            var second = MenuPress(menu2, onChip);
            Check(second.Handled && vm.IsEqualizerOn && vm.IsEqualizerInUse(chosen, AppSettings.EqualizerInPeace) && settings.EqualizerPreset == chosen.Name,
                $"after the reload a double-click turns {chosen.Name} back on: {State()}");

            var legacy = new AppSettings { EqualizerPreset = "Old", EqualizerFolder = AppSettings.EqualizerInOwnFolder };
            Check(legacy.LastEqualizer == ("Old", AppSettings.EqualizerInOwnFolder), $"an older settings file (no last preset): LastEqualizer {legacy.LastEqualizer}");
        }
        LetDoubleClickLapse();

        // ---------------------------------------------------------- 6. last preset's file gone
        Console.WriteLine("6. the last preset's file gone:");
        {
            var menu = Click(out _);
            MenuPress(menu, onChip);   // off
            LetDoubleClickLapse();
            const string gone = "No Such Preset (ThemeLab)";
            settings.EqualizerLastPreset = gone;
            settings.EqualizerLastFolder = AppSettings.EqualizerInPeace;
            var diskBefore = File.ReadAllBytes(scratch);
            var menu2 = Click(out _);
            var second = MenuPress(menu2, onChip);
            Check(!vm.IsEqualizerOn && vm.StatusText == $"Couldn't find the EQ preset {gone}" && DotIs("color.status.eqOff"),
                $"stays off: {State()}; status '{vm.StatusText}'; press handled {second.Handled}, menu IsOpen {menu2.IsOpen}");
            Check(File.ReadAllBytes(scratch).SequenceEqual(diskBefore), "settings file not written");
        }

        // ---------------------------------------------------------- the real settings
        var realAfter = Hash(realSettings);
        var realTimeAfter = File.Exists(realSettings) ? File.GetLastWriteTimeUtc(realSettings) : DateTime.MinValue;
        Check(realAfter == realBefore && realTimeAfter == realTimeBefore, $"real settings.json untouched (hash {realAfter ?? "none"}, written {realTimeAfter:u})");

        Console.WriteLine(allOk ? "ALL OK" : "SOMETHING WRONG");
        return allOk ? 0 : 1;
    }

    /// <summary>
    /// Raises the chip's Click, so the window's own handler builds the menu, and cuts
    /// the popup's IsOpen binding the moment the menu is handed to the chip, before
    /// "IsOpen = true", as <see cref="OpenEqualizerMenuClosed"/> does.
    /// </summary>
    private static ContextMenu OpenEqualizerMenuByClick(ButtonBase chip)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var hookup = typeof(ContextMenu).GetMethod("HookupParentPopup", flags)
            ?? throw new InvalidOperationException("ContextMenu.HookupParentPopup not found: not opening the menu");
        var popupField = typeof(ContextMenu).GetField("_parentPopup", flags)
            ?? throw new InvalidOperationException("ContextMenu._parentPopup not found: not opening the menu");

        ContextMenu? captured = null;
        void OnMenuSet(object? sender, EventArgs e)
        {
            if (chip.ContextMenu is not { } menu || ReferenceEquals(menu, captured))
                return;
            if (popupField.GetValue(menu) is null)
                hookup.Invoke(menu, null);
            var popup = popupField.GetValue(menu) as Popup
                ?? throw new InvalidOperationException("no popup after HookupParentPopup: not opening the menu");
            if (BindingOperations.GetBindingExpression(popup, Popup.IsOpenProperty) is null)
                throw new InvalidOperationException("the popup's IsOpen isn't bound to the menu: not opening it");
            BindingOperations.ClearBinding(popup, Popup.IsOpenProperty);
            popup.IsOpen = false;
            captured = menu;
        }

        var descriptor = DependencyPropertyDescriptor.FromProperty(FrameworkElement.ContextMenuProperty, chip.GetType());
        descriptor.AddValueChanged(chip, OnMenuSet);
        try
        {
            chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, chip));
        }
        finally
        {
            descriptor.RemoveValueChanged(chip, OnMenuSet);
        }

        return captured ?? throw new InvalidOperationException("the chip's Click set no new menu on the chip");
    }
}
