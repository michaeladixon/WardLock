using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WardLock.Services;
using WardLock.ViewModels;

namespace WardLock.ViewModels.Services
{
    internal class SharedVaultCoordinator
    {
        private readonly List<SharedVaultService> _openVaults;
        private readonly ObservableCollection<AccountViewModel> _accounts;
        private readonly ObservableCollection<string> _openVaultNames;
        private readonly ObservableCollection<string> _addTargetOptions;
        private readonly AccountStore _store;
        private readonly Action<string>? _setStatus;

        public SharedVaultCoordinator(List<SharedVaultService> openVaults,
            ObservableCollection<AccountViewModel> accounts,
            ObservableCollection<string> openVaultNames,
            ObservableCollection<string> addTargetOptions,
            AccountStore store,
            Action<string>? setStatus)
        {
            _openVaults = openVaults;
            _accounts = accounts;
            _openVaultNames = openVaultNames;
            _addTargetOptions = addTargetOptions;
            _store = store;
            _setStatus = setStatus;
        }

        public void RegisterVault(SharedVaultService vault)
        {
            _openVaults.Add(vault);
            _openVaultNames.Add(vault.VaultName);
            _addTargetOptions.Add(vault.VaultName);

            foreach (var acct in vault.Accounts)
                _accounts.Add(new AccountViewModel(acct));

            vault.ExternalChange += () => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                var old = _accounts.Where(a => a.VaultName == vault.VaultName).ToList();
                foreach (var vm in old)
                    _accounts.Remove(vm);

                foreach (var acct in vault.Accounts)
                    _accounts.Add(new AccountViewModel(acct));

                _setStatus?.Invoke($"Vault '{vault.VaultName}' updated by another user.");
            });
        }
    }
}
