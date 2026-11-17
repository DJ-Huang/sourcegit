using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class MrAssistant : ChromelessWindow
    {
        public MrAssistant()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            (DataContext as ViewModels.MrAssistant)?.Cancel();
        }

        private async void OnGenerateClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.MrAssistant vm)
                await vm.GenerateAsync();

            e.Handled = true;
        }

        private async void OnCreateClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.MrAssistant vm)
                return;

            var result = await vm.CreateAsync();
            if (result.IsSuccess)
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null && !string.IsNullOrEmpty(result.WebUrl))
                    await topLevel.Launcher.LaunchUriAsync(new Uri(result.WebUrl));

                Close();
            }
            else
            {
                vm.AppendError(result.Error);
            }

            e.Handled = true;
        }
    }
}
