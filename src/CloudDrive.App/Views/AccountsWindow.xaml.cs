using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using CloudDrive.App.Services;
using CloudDrive.Core.Models;
using CloudDrive.Core.Providers;

namespace CloudDrive.App.Views;

[SupportedOSPlatform("windows")]
public partial class AccountsWindow : Window
{
    private readonly AppController _controller;

    public AccountsWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var selectedId = (AccountList.SelectedItem as AccountRow)?.Account.Id;

        AccountList.ItemsSource = _controller.Accounts
            .Select(a => new AccountRow(
                a, _controller.Mappings.Count(m => m.Mapping.AccountId == a.Id)))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (selectedId is { } id)
        {
            AccountList.SelectedItem = AccountList.Items
                .OfType<AccountRow>()
                .FirstOrDefault(r => r.Account.Id == id);
        }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        if (new AccountEditWindow(_controller, null) { Owner = this }.ShowDialog() == true)
        {
            await _controller.RefreshAsync();
            Reload();
        }
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not AccountRow row) return;
        if (new AccountEditWindow(_controller, row.Account) { Owner = this }.ShowDialog() == true)
        {
            await _controller.RefreshAsync();
            Reload();
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not AccountRow row) return;

        // Deleting an account cascades to its mappings, which is a bigger action than the button
        // suggests — so the count is spelled out rather than left to be discovered.
        var warning = row.MappingCount == 0
            ? $"Delete the account '{row.Name}'?"
            : $"Delete the account '{row.Name}' and the {row.MappingCount} mapping(s) that use it?\n\n"
              + "Those mappings will be unmounted. Nothing on the remote storage is touched.";

        if (MessageBox.Show(warning, "CloudDrive", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _controller.DeleteAccountAsync(row.Account.Id);
            await _controller.RefreshAsync();
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Deleting the account failed.\n\n{ex.Message}", "CloudDrive",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>One row: the account, plus how many mappings depend on it.</summary>
    internal sealed class AccountRow(Account account, int mappingCount)
    {
        public Account Account { get; } = account;

        public string Name => Account.Name;

        public string ProviderName => ProviderCatalog.Get(Account.Provider).DisplayName;

        public string Summary => Account.Summary;

        public int MappingCount { get; } = mappingCount;

        public string StatusText => Account.NeedsReauth ? "Sign in again" : "Ready";

        public Brush StatusBrush => Account.NeedsReauth
            ? Application.Current?.TryFindResource("CD.Brush.Danger") as Brush ?? Brushes.Red
            : Application.Current?.TryFindResource("CD.Brush.Success") as Brush ?? Brushes.LimeGreen;
    }
}
