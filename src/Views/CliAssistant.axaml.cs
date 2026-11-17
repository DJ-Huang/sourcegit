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

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            (DataContext as ViewModels.CliAssistant)?.Cancel();
        }

        private async void OnGenerateClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.CliAssistant vm)
                await vm.GenAsync();

            e.Handled = true;
        }

        private void OnUseClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.CliAssistant vm && !string.IsNullOrEmpty(vm.Response))
                vm.Use(vm.Response);

            Close();
            e.Handled = true;
        }

        private async void OnCreateBranchAndCommitClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.CliAssistant vm)
                return;

            var succ = await vm.CreateBranchAndCommitAsync();
            if (succ)
            {
                vm.Use(string.Empty);
                Close();
            }
            else
            {
                vm.AppendError("[ERROR] Failed to create the branch or commit. Check the logs for details.");
            }

            e.Handled = true;
        }
    }
}
