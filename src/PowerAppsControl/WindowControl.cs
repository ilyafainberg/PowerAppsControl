// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — Window control + "Under Agent Control" overlay frames
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Provides the machinery behind the control_window / release_window tools:
//    • Makes a target window top-most (recording its prior state so it can be
//      restored exactly) and brings it forward.
//    • Draws a crimson border around it and a "Under Agent Control" tag with an
//      ✕ button above its titlebar, so the user can SEE which window the agent
//      is driving and reclaim it at any time by clicking ✕.
//    • Follows the target as it moves / resizes, hides while it is minimized,
//      and restores everything (border removed, top-most reverted) on release
//      or on process exit.
//
//  The overlays are WPF windows, so they run on a dedicated STA thread with its
//  own Dispatcher and a follow-timer. All public methods marshal onto that
//  thread. Overlay windows carry no title text and the WS_EX_TOOLWINDOW style,
//  so they never appear in the agent's own window enumeration.
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PowerAppsControl;

internal static class WindowControl
{
    /// <summary>Bookkeeping for one window currently under agent control.</summary>
    private sealed class Controlled
    {
        public required IntPtr Target;
        public required string Title;
        public bool WasTopmost;
        public OverlayBorder Border = null!;
        public OverlayTag Tag = null!;
        public bool OverlaysVisible = true;
        public Win32.RECT LastRect;
        public bool HasLastRect;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, Controlled> Controlled_ = new();
    private static Thread? UiThread;
    private static Dispatcher? UiDispatcher;
    private static DispatcherTimer? FollowTimer;
    private static bool ExitHookInstalled;

    // -------------------------------------------------------------------------
    //  Public API (thread-safe; marshals onto the UI dispatcher)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Put a window under agent control: record its top-most state, force it
    /// top-most, bring it forward, and show the crimson frame + tag. Idempotent —
    /// re-acquiring an already-controlled window just re-asserts foreground.
    /// </summary>
    public static string Acquire(IntPtr hwnd, string title)
    {
        EnsureUiThread();
        return UiDispatcher!.Invoke(() =>
        {
            lock (Gate)
            {
                if (Controlled_.TryGetValue(hwnd, out var existing))
                {
                    Win32.SetForegroundWindow(hwnd);
                    return $"Window '{existing.Title}' is already under agent control.";
                }

                int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
                bool wasTopmost = (ex & Win32.WS_EX_TOPMOST) != 0;

                var c = new Controlled { Target = hwnd, Title = title, WasTopmost = wasTopmost };

                // Force top-most and bring forward (best-effort foreground).
                Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                Win32.SetForegroundWindow(hwnd);

                c.Border = new OverlayBorder();
                c.Tag = new OverlayTag(title);
                // User clicking ✕ is an explicit ABORT: cancel in-flight operations and
                // arm the one-shot abort flag, THEN release the window.
                c.Tag.CloseRequested += () =>
                {
                    AgentSession.SignalUserAbort(title);
                    Release(hwnd);
                };
                c.Border.Show();
                c.Tag.Show();

                Controlled_[hwnd] = c;
                PositionOverlays(c, force: true);

                FollowTimer!.Start();
                return $"Window '{title}' is now UNDER AGENT CONTROL (top-most, crimson frame shown).";
            }
        });
    }

    /// <summary>Release one controlled window: remove its overlays and restore its prior top-most state.</summary>
    public static bool Release(IntPtr hwnd)
    {
        if (UiDispatcher is null) return false;
        return UiDispatcher.Invoke(() => ReleaseOnUi(hwnd, restore: true));
    }

    /// <summary>
    /// Update the integrated status pill on a controlled window's frame: the live status line
    /// (e.g. "Run 2/5 · step 3/8: click Submit") and whether a REC dot should pulse. This is how
    /// the recording indicator + progress live ON the frame instead of a separate HUD window.
    /// </summary>
    public static void SetStatus(IntPtr hwnd, string? status, bool recording)
    {
        if (UiDispatcher is null) return;
        UiDispatcher.Invoke(() =>
        {
            lock (Gate)
            {
                if (Controlled_.TryGetValue(hwnd, out var c))
                {
                    c.Tag.SetStatus(status, recording);
                    PositionOverlays(c, force: true); // width may have changed with the new text
                }
            }
        });
    }

    /// <summary>Release every controlled window. Returns how many were released.</summary>
    public static int ReleaseAll()
    {
        if (UiDispatcher is null) return 0;
        return UiDispatcher.Invoke(() =>
        {
            lock (Gate)
            {
                var handles = Controlled_.Keys.ToList();
                foreach (var h in handles) ReleaseOnUi(h, restore: true);
                return handles.Count;
            }
        });
    }

    /// <summary>Titles of all windows currently under agent control (for status reporting).</summary>
    public static IReadOnlyList<string> ListControlled()
    {
        lock (Gate) return Controlled_.Values.Select(c => c.Title).ToList();
    }

    /// <summary>True when at least one window is currently under agent control.</summary>
    public static bool AnyControlled()
    {
        lock (Gate) return Controlled_.Count > 0;
    }

    /// <summary>True when the specified window handle is currently under agent control.</summary>
    public static bool IsControlled(IntPtr hwnd)
    {
        lock (Gate) return Controlled_.ContainsKey(hwnd);
    }

    // -------------------------------------------------------------------------
    //  UI-thread internals
    // -------------------------------------------------------------------------

    private static bool ReleaseOnUi(IntPtr hwnd, bool restore)
    {
        lock (Gate)
        {
            if (!Controlled_.TryGetValue(hwnd, out var c)) return false;

            try { c.Border.Close(); } catch { /* already gone */ }
            try { c.Tag.Close(); } catch { /* already gone */ }

            // Revert top-most only if the window was NOT top-most before we grabbed it.
            if (restore && !c.WasTopmost && Win32.IsWindow(hwnd))
            {
                Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }

            Controlled_.Remove(hwnd);
            if (Controlled_.Count == 0) FollowTimer?.Stop();
            return true;
        }
    }

    /// <summary>Timer tick: follow each target, hide/show with its minimized state, drop dead windows.</summary>
    private static void Follow(object? sender, EventArgs e)
    {
        lock (Gate)
        {
            List<IntPtr>? dead = null;
            foreach (var c in Controlled_.Values)
            {
                if (!Win32.IsWindow(c.Target)) { (dead ??= new()).Add(c.Target); continue; }

                bool hidden = Win32.IsIconic(c.Target) || !Win32.IsWindowVisible(c.Target);
                if (hidden)
                {
                    if (c.OverlaysVisible)
                    {
                        c.Border.Hide();
                        c.Tag.Hide();
                        c.OverlaysVisible = false;
                    }
                    continue;
                }

                if (!c.OverlaysVisible)
                {
                    c.Border.Show();
                    c.Tag.Show();
                    c.OverlaysVisible = true;
                }

                PositionOverlays(c, force: false);
            }

            if (dead is not null)
                foreach (var h in dead) ReleaseOnUi(h, restore: false); // window gone; nothing left to restore
        }
    }

    /// <summary>
    /// Draw the crimson frame HUGGING the target window's border (thin, rounded) and the
    /// integrated status pill attached to its top edge. Then re-assert top-most z-order.
    ///
    ///  • NORMAL window   — frame outset by 1px (hugs the edge), rounded corners; the pill
    ///                      hangs as a tab on the top-left, its bottom sitting on the frame.
    ///  • MAXIMIZED window— frame clamped to the monitor and drawn INSET (so it stays on
    ///                      screen); the pill sits just inside the top-left.
    /// </summary>
    private static void PositionOverlays(Controlled c, bool force)
    {
        if (!Win32.GetWindowRect(c.Target, out var r)) return;

        bool moved = force || !c.HasLastRect ||
                     r.Left != c.LastRect.Left || r.Top != c.LastRect.Top ||
                     r.Right != c.LastRect.Right || r.Bottom != c.LastRect.Bottom;
        c.LastRect = r;
        c.HasLastRect = true;

        uint dpi = Win32.GetDpiForWindow(c.Target);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int pad  = (int)Math.Round(1 * scale);    // how far the frame sits outside the window edge
        int tagH = (int)Math.Round(30 * scale);

        var borderH = new WindowInteropHelper(c.Border).Handle;
        var tagHwnd = new WindowInteropHelper(c.Tag).Handle;

        uint zFlags = Win32.SWP_NOACTIVATE;

        bool maximized = Win32.IsZoomed(c.Target);
        c.Border.SetMaximized(maximized, scale);
        c.Tag.SetMaximized(maximized);

        int fx, fy, fw, fh;   // frame rect (physical px)

        if (maximized)
        {
            var vis = VisibleBounds(c.Target, r);
            int bt = (int)Math.Round(2 * scale);
            fx = vis.Left + bt;
            fy = vis.Top + bt;
            fw = Math.Max(1, vis.Width - bt * 2);
            fh = Math.Max(1, vis.Height - bt * 2);
        }
        else
        {
            // Hug: outset by just `pad` so the frame sits right on the window's edge.
            fx = r.Left - pad;
            fy = r.Top - pad;
            fw = (r.Right - r.Left) + pad * 2;
            fh = (r.Bottom - r.Top) + pad * 2;
        }

        // Pill width: fit the content but clamp to the frame width.
        int tagW = c.Tag.MeasurePhysicalWidth(scale, maxPx: Math.Max(160, fw - (int)Math.Round(8 * scale)));

        int tx, ty;
        if (maximized)
        {
            tx = fx + (int)Math.Round(6 * scale);
            ty = fy + (int)Math.Round(6 * scale);
        }
        else
        {
            // Tab on the top-left: its bottom edge rests on the frame's top border.
            tx = fx + (int)Math.Round(4 * scale);
            ty = fy - tagH + pad;
        }

        Win32.SetWindowPos(borderH, Win32.HWND_TOPMOST, fx, fy, fw, fh, zFlags | (moved ? 0 : Win32.SWP_NOMOVE | Win32.SWP_NOSIZE));
        // The pill always re-sizes (its width tracks the status text), so never NOSIZE it.
        Win32.SetWindowPos(tagHwnd, Win32.HWND_TOPMOST, tx, ty, tagW, tagH, zFlags);
    }

    /// <summary>
    /// The on-screen visible bounds for a window. For a maximized window GetWindowRect
    /// overshoots each monitor edge by the frame padding, so we intersect it with the
    /// monitor's work area to get the rectangle the user actually sees.
    /// </summary>
    private static (int Left, int Top, int Width, int Height) VisibleBounds(IntPtr hwnd, Win32.RECT r)
    {
        var mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (mon != IntPtr.Zero && Win32.GetMonitorInfo(mon, ref mi))
        {
            int left   = Math.Max(r.Left, mi.rcWork.Left);
            int top    = Math.Max(r.Top, mi.rcWork.Top);
            int right  = Math.Min(r.Right, mi.rcWork.Right);
            int bottom = Math.Min(r.Bottom, mi.rcWork.Bottom);
            return (left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }
        return (r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    // -------------------------------------------------------------------------
    //  Dedicated STA UI thread + dispatcher
    // -------------------------------------------------------------------------

    private static void EnsureUiThread()
    {
        lock (Gate)
        {
            if (UiThread is not null) return;

            using var ready = new ManualResetEventSlim(false);
            UiThread = new Thread(() =>
            {
                UiDispatcher = Dispatcher.CurrentDispatcher;
                FollowTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(75),
                };
                FollowTimer.Tick += Follow;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "PowerAppsControl-Overlay-UI",
            };
            UiThread.SetApartmentState(ApartmentState.STA);
            UiThread.Start();
            ready.Wait();

            if (!ExitHookInstalled)
            {
                // Safety net: if the server dies while windows are controlled,
                // un-top-most them so nothing is left stuck above everything.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreAllOnExit();
                ExitHookInstalled = true;
            }
        }
    }

    /// <summary>
    /// Process-exit cleanup. Overlays vanish with the process, so we only need
    /// to revert top-most state. Runs synchronously on whatever thread is
    /// tearing the process down — SetWindowPos is safe to call cross-thread.
    /// </summary>
    private static void RestoreAllOnExit()
    {
        lock (Gate)
        {
            foreach (var c in Controlled_.Values)
            {
                if (!c.WasTopmost && Win32.IsWindow(c.Target))
                {
                    Win32.SetWindowPos(c.Target, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                }
            }
            Controlled_.Clear();
        }
    }

    // -------------------------------------------------------------------------
    //  Overlay windows (WPF)
    // -------------------------------------------------------------------------

    /// <summary>Click-through rounded crimson frame that hugs the controlled window's border.</summary>
    private sealed class OverlayBorder : Window
    {
        private readonly Border frame;

        public OverlayBorder()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            ShowActivated = false;
            // Start fully off-screen so the very first paint isn't visible; PositionOverlays
            // relocates it into place before the user can see a flicker at the default origin.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000; Top = -32000; Width = 1; Height = 1;

            frame = new Border
            {
                BorderBrush = PacTheme.Accent,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(9),
                Background = Brushes.Transparent, // hollow centre → target stays visible
            };
            Content = frame;

            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
                ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT
                    | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
                Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, ex);
            };
        }

        /// <summary>Slightly thicker + larger radius when maximized so the inset frame still reads.</summary>
        public void SetMaximized(bool maximized, double scale)
        {
            frame.BorderThickness = new Thickness(maximized ? 2 : 2);
            frame.CornerRadius = new CornerRadius(maximized ? 10 : 9);
        }
    }

    /// <summary>
    /// Integrated status pill that sits ON the frame's top edge: a pulsing REC dot (while
    /// recording), the "Under Agent Control" label with the live status line, and a nicely
    /// styled ✕ close button. Replaces the old separate top-center HUD.
    /// </summary>
    private sealed class OverlayTag : Window
    {
        private readonly Border shell;
        private readonly Ellipse recDot;
        private readonly TextBlock label;
        private readonly TextBlock statusText;
        private System.Windows.Media.Animation.DoubleAnimation? pulse;
        private bool recording;

        public event Action? CloseRequested;

        public OverlayTag(string title)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            SnapsToDevicePixels = true;
            // Start off-screen to avoid a flicker at the default origin before positioning.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000; Top = -32000; Width = 1; Height = 1;

            recDot = new Ellipse
            {
                Width = 9, Height = 9,
                Fill = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 8, 0),
                Visibility = Visibility.Collapsed,
            };

            label = new TextBlock
            {
                Text = "Under Agent Control",
                Foreground = Brushes.White,
                FontFamily = PacTheme.Font,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip = title,
            };

            statusText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
                FontFamily = PacTheme.Font,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed,
            };

            var sep = new Border
            {
                Width = 1, Height = 14,
                Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            statusSep = sep;

            var close = BuildCloseButton();

            var panel = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Stretch };
            DockPanel.SetDock(recDot, Dock.Left);
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(sep, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);
            panel.Children.Add(recDot);
            panel.Children.Add(label);
            panel.Children.Add(sep);
            panel.Children.Add(close);
            panel.Children.Add(statusText); // fills remaining space

            shell = new Border
            {
                Background = PacTheme.Accent,
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Child = panel,
            };
            Content = shell;

            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
                ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
                Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, ex);
            };
        }

        private Border statusSep = null!;

        /// <summary>A borderless ✕ with a tasteful hover: soft white overlay circle, white glyph — no blue.</summary>
        private Button BuildCloseButton()
        {
            var glyph = new TextBlock
            {
                Text = "✕",
                Foreground = Brushes.White,
                FontFamily = PacTheme.Font,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var bg = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Width = 22, Height = 22,
                Margin = new Thickness(6, 4, 6, 4),
                Child = glyph,
            };

            var btn = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Stop & release this window",
                Focusable = false,
            };

            // Strip the default (blue) button chrome entirely — the button IS the bordered box.
            var template = new ControlTemplate(typeof(Button));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            template.VisualTree = cp;
            btn.Template = template;
            btn.Content = bg;

            var softWhite = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            btn.MouseEnter += (_, _) => bg.Background = softWhite;
            btn.MouseLeave += (_, _) => bg.Background = Brushes.Transparent;
            btn.PreviewMouseLeftButtonDown += (_, _) => bg.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            btn.Click += (_, _) => CloseRequested?.Invoke();
            return btn;
        }

        /// <summary>Update the live status line and toggle/pulse the REC dot.</summary>
        public void SetStatus(string? status, bool recording)
        {
            this.recording = recording;
            recDot.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
            if (recording)
            {
                if (pulse is null)
                {
                    pulse = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(750))
                    {
                        AutoReverse = true,
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    };
                }
                recDot.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            else
            {
                recDot.BeginAnimation(UIElement.OpacityProperty, null);
            }

            bool has = !string.IsNullOrWhiteSpace(status);
            statusText.Text = status ?? "";
            statusText.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            statusSep.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Round the BOTTOM corners when maximized (the pill hangs down into the window).</summary>
        public void SetMaximized(bool maximized)
        {
            shell.CornerRadius = maximized ? new CornerRadius(0, 0, 7, 7) : new CornerRadius(7, 7, 0, 0);
        }

        /// <summary>Measure the pill's natural width in physical pixels, clamped to <paramref name="maxPx"/>.</summary>
        public int MeasurePhysicalWidth(double scale, int maxPx)
        {
            // Let the status ellipsize: measure with the label/dot/button fixed and cap the total.
            shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double dip = shell.DesiredSize.Width;
            if (dip < 1) dip = 180;
            int px = (int)Math.Ceiling(dip * scale);
            // Add a little breathing room for the status text when present.
            if (statusText.Visibility == Visibility.Visible) px += (int)Math.Round(120 * scale);
            return Math.Max((int)Math.Round(160 * scale), Math.Min(px, maxPx));
        }
    }
}

/// <summary>Shared Power CAT visual tokens for the overlay chrome (matches the report theme).</summary>
internal static class PacTheme
{
    // Power CAT accent (#D85A86) used across the report + overlay for a consistent look.
    public static readonly SolidColorBrush Accent = new(Color.FromRgb(0xD8, 0x5A, 0x86));
    public static readonly FontFamily Font = new("Segoe UI");

    static PacTheme()
    {
        Accent.Freeze();
    }
}

/// <summary>
/// Central enforcement for the "you must select a window before you can capture or
/// interact with it" policy. Capture and interaction tools consult this gate; when it
/// blocks, they return the instruction string instead of performing the action.
///
/// Enforcement is ON by default and can be disabled by setting the environment variable
/// POWERAPPSCONTROL_REQUIRE_WINDOW_SELECTION to 0 / false / off / no.
/// </summary>
internal static class Enforcement
{
    /// <summary>Whether the selection gate is active. Reads the env var live so it can be toggled without a rebuild.</summary>
    public static bool RequireSelection
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("POWERAPPSCONTROL_REQUIRE_WINDOW_SELECTION");
            if (string.IsNullOrWhiteSpace(v)) return true; // default ON
            return v.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no" or "disabled");
        }
    }

    private static string ControlledList()
    {
        var titles = WindowControl.ListControlled();
        return titles.Count == 0 ? "(none)" : string.Join(", ", titles.Select(t => $"'{t}'"));
    }

    /// <summary>Surface a pending user-abort (consume-once), independent of the selection gate.</summary>
    public static string? CheckAbort() => AgentSession.ConsumeAbort();

    /// <summary>
    /// Gate a tool that needs SOME window under control (absolute-coordinate tools,
    /// full-screen capture, keyboard). Returns null if allowed, or an instruction
    /// string the caller should return verbatim if blocked.
    /// </summary>
    public static string? GateAny(string tool)
    {
        var abort = AgentSession.ConsumeAbort();
        if (abort is not null) return abort;
        if (!RequireSelection) return null;
        if (WindowControl.AnyControlled()) return null;
        return
            $"⛔ ACTION BLOCKED — no window is under agent control, so '{tool}' will not run.\n" +
            "You MUST select a window first. Do this:\n" +
            "  1. find_window(filter?)   → lists open windows as TEXT (no screenshot needed).\n" +
            "  2. control_window(query)  → selects + pins the target window (a crimson\n" +
            "                              'Under Agent Control' frame appears on it).\n" +
            $"Then retry '{tool}'. This safeguard stops the agent from acting on the wrong or\n" +
            "background window. When finished, call release_window(query) or release_window(all=true).";
    }

    /// <summary>
    /// Gate a tool that targets a SPECIFIC window (per-window capture / inspection /
    /// window-relative click). Returns null if allowed, or an instruction string if the
    /// target window has not been selected via control_window.
    /// </summary>
    public static string? GateTarget(bool isControlled, string title, string tool)
    {
        var abort = AgentSession.ConsumeAbort();
        if (abort is not null) return abort;
        if (!RequireSelection) return null;
        if (isControlled) return null;
        return
            $"⛔ ACTION BLOCKED — the window '{title}' is not under agent control, so '{tool}' will not run.\n" +
            $"'{tool}' can only target a window you have explicitly selected. Call:\n" +
            $"  control_window(query='{title}')\n" +
            $"then retry '{tool}'. This pins the window top-most and frames it so the user can see what\n" +
            $"you are doing. Currently under agent control: {ControlledList()}.";
    }
}
