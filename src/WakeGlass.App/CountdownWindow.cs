using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WakeGlass.App;

public sealed class CountdownWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public CountdownWindow(DateTime wake, int seconds)
    {
        Title = "WakeGlass · 即将睡眠"; Width = 460; Height = 310; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = true; FontFamily = new FontFamily("Segoe UI Variable, Microsoft YaHei UI");
        SetResourceReference(ForegroundProperty, "Text"); Background = Theme.Dark ? new SolidColorBrush(Color.FromRgb(30, 29, 35)) : Brushes.WhiteSmoke;
        int remaining = Math.Clamp(seconds, 5, 60);
        var stack = new StackPanel { Margin = new Thickness(32) };
        var title = new TextBlock { Text = "即将进入睡眠", FontSize = 23, FontWeight = FontWeights.SemiBold }; stack.Children.Add(title);
        var count = new TextBlock { Text = $"{remaining} 秒", FontSize = 32, Margin = new Thickness(0, 12, 0, 8) }; stack.Children.Add(count);
        stack.Children.Add(new TextBlock { Text = $"已验证唤醒任务：{wake:MM月dd日 HH:mm}\n唤醒仍受固件、电源和 Windows 策略影响。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var cancel = new Button { Content = "取消本次睡眠", Padding = new Thickness(20, 10, 20, 10), IsCancel = true, IsDefault = true }; cancel.Click += (_, _) => { DialogResult = false; }; stack.Children.Add(cancel); Content = stack;
        timer.Tick += (_, _) => { remaining--; count.Text = $"{remaining} 秒"; if (remaining <= 0) { timer.Stop(); DialogResult = true; } };
        Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop();
    }
}
