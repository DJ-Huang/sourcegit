using System;
using System.Collections.Generic;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CommitAssistant : ObservableObject
    {
        public List<AI.Service> ApiServices { get; }
        public List<AI.Service> CliServices { get; }
        public bool HasApiServices => ApiServices.Count > 0;
        public bool HasCliServices => CliServices.Count > 0;
        public bool HasApiSelection => _selectedApiService != null;

        public AIAssistant Api
        {
            get => _api;
            private set => SetProperty(ref _api, value);
        }

        public CliAssistant Cli
        {
            get => _cli;
            private set => SetProperty(ref _cli, value);
        }

        public int SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value))
                    StartApiIfNeeded();
            }
        }

        public AI.Service SelectedApiService
        {
            get => _selectedApiService;
            set
            {
                if (!SetProperty(ref _selectedApiService, value))
                    return;

                Api?.Cancel();
                Api = null;
                OnPropertyChanged(nameof(HasApiSelection));
                StartApiIfNeeded();
            }
        }

        public AI.Service SelectedCliService
        {
            get => _selectedCliService;
            set
            {
                if (!SetProperty(ref _selectedCliService, value))
                    return;

                Cli?.Cancel();
                Cli = value == null ? null : new CliAssistant(_repo, value, _changes);
                OnPropertyChanged(nameof(HasCliSelection));
            }
        }

        public bool HasCliSelection => _selectedCliService != null;

        public CommitAssistant(Repository repo, List<Models.Change> changes, AI.Service selected = null)
        {
            _repo = repo;
            _changes = [.. changes];
            var preferred = repo.Settings.PreferredOpenAIService;
            var apiServices = new List<AI.Service>();
            foreach (var service in Preferences.Instance.OpenAIServices)
            {
                if (!service.IsCliBackend)
                    apiServices.Add(service);
            }

            var preferredApi = apiServices.Find(s => string.Equals(s.Name, preferred, StringComparison.Ordinal));
            ApiServices = preferredApi != null ? [preferredApi] : apiServices;
            CliServices = Repository.GetCliServices();
            _selectedApiService = selected is { IsCliBackend: false } ? selected :
                ApiServices.Count == 1 ? ApiServices[0] : null;
            SelectedCliService = selected is { IsCliBackend: true } ? selected :
                CliServices.Find(s => string.Equals(s.Name, preferred, StringComparison.Ordinal)) ??
                (HasCliServices ? CliServices[0] : null);
            _selectedTab = selected?.IsCliBackend == true || (!HasApiServices && HasCliServices) ? 1 : 0;
        }

        public void Open()
        {
            _opened = true;
            StartApiIfNeeded();
        }

        public void Close()
        {
            _opened = false;
            Api?.Cancel();
            Cli?.Cancel();
        }

        private void StartApiIfNeeded()
        {
            if (!_opened || _selectedTab != 0 || !HasApiSelection || Api != null)
                return;

            Api = new AIAssistant(_repo, _selectedApiService, _changes);
            _ = Api.GenAsync();
        }

        private readonly Repository _repo;
        private readonly List<Models.Change> _changes;
        private bool _opened;
        private int _selectedTab;
        private AI.Service _selectedApiService;
        private AI.Service _selectedCliService;
        private AIAssistant _api;
        private CliAssistant _cli;
    }
}
