﻿using System;
using MusicBeeRemote.Core.Settings.Dialog.Commands;
using MusicBeeRemote.Core.Settings.Dialog.Converters;

namespace MusicBeeRemote.Core.Settings.Dialog.BasePanel
{
    public class ConfigurationPanelPresenter : IConfigurationPanelPresenter
    {
        private IConfigurationPanelView _view;
        private readonly ConfigurationPanelViewModel _model;
        private readonly OpenHelpCommand _openHelpCommand;
        private readonly OpenLogDirectoryCommand _openLogDirectoryCommand;
        private readonly SaveConfigurationCommand _saveConfigurationCommand;
        private readonly RefreshLibraryCommand _refreshLibraryCommand;

        public ConfigurationPanelPresenter(
            ConfigurationPanelViewModel model,
            OpenHelpCommand openHelpCommand,
            OpenLogDirectoryCommand openLogDirectoryCommand,
            SaveConfigurationCommand saveConfigurationCommand,
            RefreshLibraryCommand refreshLibraryCommand
        )
        {
            _model = model;
            _openHelpCommand = openHelpCommand;
            _openLogDirectoryCommand = openLogDirectoryCommand;
            _saveConfigurationCommand = saveConfigurationCommand;
            _refreshLibraryCommand = refreshLibraryCommand;
        }

        public void Load()
        {
            _model.PropertyChanged += OnPropertyChanged;
            CheckIfAttached();
            _view.UpdateLocalIpAddresses(_model.LocalIpAddresses);
            _view.UpdateListeningPort(_model.ListeningPort);
            _view.UpdateStatus(new SocketStatus(_model.ServiceStatus));
            _view.UpdateFirewallStatus(_model.FirewallUpdateEnabled);
            _view.UpdateLoggingStatus(_model.DebugEnabled);
            _view.UpdateFilteringData(_model.FilteringData, _model.FilteringSelection);           
            _view.UpdatePluginVersion(_model.PluginVersion);
            _view.UpdateCachedTracks(_model.CachedTracks);
        }

        private void OnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_model.ServiceStatus))
            {
                _view.UpdateStatus(new SocketStatus(_model.ServiceStatus));
            }
        }

        public void Attach(IConfigurationPanelView view)
        {
            _view = view;
        }

        public void OpenHelp()
        {
            _openHelpCommand.Execute();
        }

        public void SaveSettings()
        {
            _saveConfigurationCommand.Execute();
            _model.VerifyConnection();
        }

        public void OpenLogDirectory()
        {
            _openLogDirectoryCommand.Execute();
        }

        public void LoggingStatusChanged(bool @checked)
        {
            _model.DebugEnabled = @checked;
        }

        public void UpdateFirewallSettingsChanged(bool @checked)
        {
            _model.FirewallUpdateEnabled = @checked;
        }

        public void UpdateListeningPort(uint listeningPort)
        {
            _model.ListeningPort = listeningPort;
        }

        public void UpdateFilteringSelection(FilteringSelection selected)
        {
            _model.FilteringSelection = selected;
        }

        public void RefreshCache()
        {
            _refreshLibraryCommand.Execute();
        }

        private void CheckIfAttached()
        {
            if (_view == null)
            {
                throw new ViewNotAttachedException();
            }
        }

        private class ViewNotAttachedException : Exception
        {
            public ViewNotAttachedException() : base("View was not attached")
            {
            }
        }
    }
}