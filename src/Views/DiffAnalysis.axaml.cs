using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class DiffAnalysis : ChromelessWindow
    {
        public DiffAnalysis()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        internal static MenuItem CreateMenu(Control anchor, string path, Func<Guid, ViewModels.DiffAnalysis> create)
        {
            if (TopLevel.GetTopLevel(anchor) is not Window owner)
                return null;

            var analysis = new MenuItem() { Header = App.Text("DiffAnalysis") };
            analysis.Click += (_, e) =>
            {
                var preferred = anchor.FindAncestorOfType<Repository>()?.DataContext is ViewModels.Repository repo &&
                    repo.FullPath == path ? repo.Settings.PreferredExternalCli : Guid.Empty;
                var window = new DiffAnalysis() { DataContext = create(preferred) };
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
            return anchor.CreateAIMenu(analysis);
        }

        internal static void AddSelectionMenu(ContextMenu menu, Control anchor, string path,
            List<Models.Change> changes, Func<Models.Change, Models.DiffOption> createOption)
        {
            if (menu == null || changes.Count == 0)
                return;

            var options = changes.DistinctBy(static change => change.Path, StringComparer.Ordinal).Select(createOption).ToArray();
            AddAnalysisMenu(menu, CreateMenu(anchor, path, preferred => new(path, options, preferred)));
        }

        internal static void AddCommitMenu(ContextMenu menu, Control anchor, string path, Models.Commit commit)
        {
            AddAnalysisMenu(menu, CreateMenu(anchor, path, preferred => new(path, commit, preferred)));
        }

        private static void AddAnalysisMenu(ContextMenu menu, MenuItem analysis)
        {
            if (menu == null || analysis == null)
                return;

            if (menu.Items.Count > 0)
                menu.Items.Insert(0, new MenuItem() { Header = "-" });
            menu.Items.Insert(0, analysis);
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (DataContext is ViewModels.DiffAnalysis vm)
                await vm.PrepareAsync();
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

        private async void OnConfigureClicked(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.DiffAnalysis vm)
            {
                await new ExternalCliSettings().ShowDialog(this);
                vm.RefreshProfiles();
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

        private async void OnCopySelectionClicked(object sender, RoutedEventArgs e)
        {
            var selected = ConversationScroll.GetSelectedText();
            if (selected.Length > 0)
                await this.CopyTextAsync(selected);
        }

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
