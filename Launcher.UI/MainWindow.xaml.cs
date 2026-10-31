using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Launcher.UI.ViewModels;
using Serilog;

namespace Launcher.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) ToggleMax(); else DragMove();
    }
    private void ToggleMax() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object s, RoutedEventArgs e) => ToggleMax();
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private void AccountAction_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainViewModel;
        Log.Information("UIAction: account button clicked. MainViewModelAttached={MainViewModelAttached}, IsAuthenticated={IsAuthenticated}",
            viewModel is not null, viewModel?.IsAuthenticated == true);
    }

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        var page = ReferenceEquals(sender, ShopPage) ? "Shop" : "Profile";
        var pageElement = sender as FrameworkElement;
        var viewModel = DataContext as MainViewModel;
        if (viewModel is null) return;
        if (pageElement?.DataContext is not MainViewModel)
        {
            Log.Warning("UIBinding: page became visible without MainViewModel. Page={Page}", page);
            return;
        }
        if (!string.Equals(viewModel.CurrentPage, page, StringComparison.Ordinal)) return;

        Log.Information("UIAction: page became visible. Page={Page}, IsAuthenticated={IsAuthenticated}", page, viewModel.IsAuthenticated);
    }

    private void Telegram_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://t.me/+smNneW8fE2M3ZDEy") { UseShellExecute = true });

    private void Support_Click(object sender, RoutedEventArgs e) =>
        new Views.SupportWindow { Owner = this }.ShowDialog();

    private void AuthCopyCode_Click(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as ViewModels.MainViewModel;
        var code = vm?.AuthCodeText;
        if (string.IsNullOrWhiteSpace(code)) return;

        try
        {
            Clipboard.SetText(code);
            MessageBox.Show("Код скопирован в буфер обмена.", "Microsoft вход", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось скопировать код:\n{ex.Message}", "Microsoft вход", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
