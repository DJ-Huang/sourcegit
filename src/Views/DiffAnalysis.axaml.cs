using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class DiffAnalysis : ChromelessWindow
    {
        public DiffAnalysis()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        internal static MenuItem CreateMenu(Control anchor, Func<AI.Service, ViewModels.DiffAnalysis> create)
        {
            if (TopLevel.GetTopLevel(anchor) is not Window owner)
                return null;

            var menu = new MenuItem() { Header = App.Text("DiffAnalysis"), Icon = anchor.CreateMenuIcon("Icons.AIAssist") };
            foreach (var service in ViewModels.Repository.GetCliServices())
            {
                var item = new MenuItem() { Header = service.Name };
                item.Click += (_, e) =>
                {
                    var window = new DiffAnalysis() { DataContext = create(service) };
                    var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                    if (screen != null)
                    {
                        var availableWidth = screen.WorkingArea.Width / screen.Scaling - 24;
                        var availableHeight = screen.WorkingArea.Height / screen.Scaling - 24;
                        window.Width = Math.Clamp(availableWidth, window.MinWidth, window.Width);
                        window.Height = Math.Clamp(availableHeight, window.MinHeight, window.Height);
                    }

                    window.Show(owner);
                    window.Activate();
                    e.Handled = true;
                };
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
                menu.Items.Add(new MenuItem() { Header = App.Text("AIAssistant.NoCliServices"), IsEnabled = false });

            return menu;
        }

        internal static void AddSelectionMenu(ContextMenu menu, Control anchor, string path,
            List<Models.Change> changes, Func<Models.Change, Models.DiffOption> createOption)
        {
            if (menu == null || changes.Count == 0)
                return;

            var options = changes.DistinctBy(change => change.Path, StringComparer.Ordinal).Select(createOption).ToArray();
            var analysis = CreateMenu(anchor, service => new(path, service, options));
            if (analysis == null)
                return;

            if (menu.Items.Count > 0)
                menu.Items.Insert(0, new MenuItem() { Header = "-" });
            menu.Items.Insert(0, analysis);
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (DataContext is ViewModels.DiffAnalysis vm)
                await vm.AskAsync();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (!e.Cancel)
                (DataContext as ViewModels.DiffAnalysis)?.Stop();
        }

        private async void OnAskClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.DiffAnalysis vm)
            {
                ConversationScroll.ScrollToEnd();
                await vm.AskAsync();
            }
        }

        private void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (DataContext is not ViewModels.DiffAnalysis { IsBusy: true } ||
                sender is not ScrollViewer scroll || e.ExtentDelta.Y <= 0)
                return;

            var previousBottom = scroll.Extent.Height - e.ExtentDelta.Y - scroll.Viewport.Height;
            if (previousBottom - scroll.Offset.Y < 24)
                Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Loaded);
        }

        private void OnStopClicked(object sender, RoutedEventArgs e) =>
            (DataContext as ViewModels.DiffAnalysis)?.Stop();

        private async void OnCopyMarkdownClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.DiffAnalysis vm)
                await this.CopyTextAsync(vm.GetAnswerMarkdown());
        }

        private async void OnCopyTranscriptClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.DiffAnalysis vm)
                await this.CopyTextAsync(vm.GetTranscript());
        }
    }
}
