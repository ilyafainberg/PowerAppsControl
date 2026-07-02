// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — Live test HUD (top-center status pill)
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  A single always-on-top pill anchored to the top-center of the primary monitor
//  that tells the user, in plain language, exactly what the test rig is doing:
//  a red REC dot, the app name, the mode (SMOKE / scripted), the current run
//  (e.g. "Run 2/5"), the window index for load runs, and the current step text.
//  It complements the per-window crimson "Under Agent Control" frames: the frames
//  say WHICH windows are being driven, the HUD says WHAT is happening right now.
//
//  Runs on its own dedicated STA thread + Dispatcher (WPF overlay), mirroring the
//  WindowControl overlay pattern. All public methods marshal onto that thread.
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PowerAppsControl;

internal sealed class TestHud
{
    private Thread? uiThread;
    private Dispatcher? dispatcher;
    private PillWindow? pill;
    private readonly object gate = new();

    /// <summary>Show the HUD with an initial headline (e.g. the app name). Idempotent.</summary>
    public void Show(string appName, string mode)
    {
        EnsureUiThread();
        dispatcher!.Invoke(() =>
        {
            pill ??= new PillWindow();
            pill.SetHeadline(appName, mode);
            pill.SetStatus("Starting session…");
            pill.Show();
            pill.Reposition();
        });
    }

    /// <summary>Update the live status line (current run / window / step).</summary>
    public void SetStatus(string status)
    {
        if (dispatcher is null) return;
        dispatcher.Invoke(() =>
        {
            pill?.SetStatus(status);
            pill?.Reposition();
        });
    }

    /// <summary>Flip the pill to a green "done" state before it is hidden.</summary>
    public void SetDone(string status)
    {
        if (dispatcher is null) return;
        dispatcher.Invoke(() =>
        {
            pill?.SetDone(status);
            pill?.Reposition();
        });
    }

    /// <summary>Hide and dispose the HUD.</summary>
    public void Hide()
    {
        if (dispatcher is null) return;
        dispatcher.Invoke(() =>
        {
            try { pill?.Close(); } catch { /* already gone */ }
            pill = null;
        });
    }

    private void EnsureUiThread()
    {
        lock (gate)
        {
            if (uiThread is not null) return;
            using var ready = new ManualResetEventSlim(false);
            uiThread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "PowerAppsControl-HUD-UI",
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            ready.Wait();
        }
    }

    /// <summary>The pill window itself. Click-through, no taskbar entry, top-most.</summary>
    private sealed class PillWindow : Window
    {
        private readonly Ellipse recDot;
        private readonly TextBlock headline;
        private readonly TextBlock status;
        private readonly Border shell;

        public PillWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            IsHitTestVisible = false;

            recDot = new Ellipse
            {
                Width = 11, Height = 11,
                Fill = Brushes.Red,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
            };
            // Pulse the REC dot so it's obviously "live".
            var pulse = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            recDot.BeginAnimation(UIElement.OpacityProperty, pulse);

            headline = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            status = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xE4, 0xFF)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };

            var sep = new Border
            {
                Width = 1, Height = 16,
                Background = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(10, 0, 0, 0),
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(recDot);
            panel.Children.Add(headline);
            panel.Children.Add(sep);
            panel.Children.Add(status);

            shell = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x1B, 0x2E)),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(16, 8, 18, 8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0)),
                BorderThickness = new Thickness(1),
                Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45,
                },
            };
            Content = shell;

            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
                ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;
                Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, ex);
            };
        }

        public void SetHeadline(string appName, string mode)
        {
            var modeLabel = mode.Equals("smoke", StringComparison.OrdinalIgnoreCase) ? "SMOKE TEST" : "UX TEST";
            headline.Text = $"{modeLabel} · {appName}";
        }

        public void SetStatus(string text) => status.Text = text;

        public void SetDone(string text)
        {
            recDot.BeginAnimation(UIElement.OpacityProperty, null);
            recDot.Opacity = 1;
            recDot.Fill = new SolidColorBrush(Color.FromRgb(0x36, 0xC7, 0x6A));
            shell.BorderBrush = new SolidColorBrush(Color.FromRgb(0x36, 0xC7, 0x6A));
            status.Text = text;
        }

        /// <summary>Center the pill horizontally near the top of the primary work area.</summary>
        public void Reposition()
        {
            UpdateLayout();
            double w = ActualWidth > 0 ? ActualWidth : 320;
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - w) / 2;
            Top = wa.Top + 12;
        }
    }
}
