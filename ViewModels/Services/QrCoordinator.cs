using System;
using System.Collections.ObjectModel;
using WardLock.Services;
using WardLock.Models;

namespace WardLock.ViewModels.Services
{
    internal class QrCoordinator
    {
        private readonly AccountStore _store;
        private readonly ObservableCollection<AccountViewModel> _accounts;
        private readonly Func<SharedVaultService?> _getSelectedVault;
        private readonly Action<string>? _setStatus;

        public QrCoordinator(AccountStore store, ObservableCollection<AccountViewModel> accounts, Func<SharedVaultService?> getSelectedVault, Action<string>? setStatus)
        {
            _store = store;
            _accounts = accounts;
            _getSelectedVault = getSelectedVault;
            _setStatus = setStatus;
        }

        // Provide wrapper methods for QR scanning flows if needed in future refactor
    }
}
