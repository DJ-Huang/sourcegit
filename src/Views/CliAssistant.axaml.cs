using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class CliAssistant : ChromelessWindow
    {
        public CliAssistant()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (DataContext is ViewModels.CliAssistant vm)
                await vm.GenAsync();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            (DataContext as ViewModels.CliAssistant)?.Cancel();
        }

        private void OnUseClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.CliAssistant vm && !string.IsNullOrEmpty(vm.Response))
                vm.Use(vm.Response);

            Close();
            e.Handled = true;
        }

        private async void OnRegenClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.CliAssistant vm)
                await vm.GenAsync();

            e.Handled = true;
        }
    }
}
