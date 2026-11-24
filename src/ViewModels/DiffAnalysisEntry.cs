using System.Text;

using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class DiffAnalysisEntry : ObservableObject
    {
        public string Heading { get; }
        public string Details { get; }
        public TextDocument DetailsDocument => new(Details ?? string.Empty);
        public string AnswerMarkdown => IsAnswer ? _markdown : null;
        public bool IsAnswer => _type == "answer";
        public bool IsNotice => _type == "notice";
        public bool IsTrace => !IsAnswer && !IsNotice;
        public bool HasDetails => !string.IsNullOrEmpty(Details);
        public bool HasMarkdown => !string.IsNullOrEmpty(_markdown);

        public string Markdown
        {
            get => _markdown;
            private set
            {
                if (SetProperty(ref _markdown, value))
                    OnPropertyChanged(nameof(HasMarkdown));
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                    OnPropertyChanged(nameof(ExpandedEntry));
            }
        }

        public DiffAnalysisEntry ExpandedEntry => _isExpanded ? this : null;

        internal DiffAnalysisEntry(AI.CliTraceEvent entry)
        {
            _type = entry.Type;
            Heading = GetHeading(entry);
            _markdown = IsAnswer ? entry.Payload : entry.Thinking;
            Details = IsTrace ? entry.Payload : null;
        }

        internal void AppendThinking(string text) => Markdown += text;

        internal void WriteTo(StringBuilder buffer)
        {
            if (buffer.Length > 0)
                buffer.Append("\n\n");

            if (!IsAnswer)
                buffer.Append(Heading);
            if (HasMarkdown)
            {
                if (!IsAnswer)
                    buffer.Append('\n');
                buffer.Append(Markdown);
            }

            if (HasDetails)
                buffer.Append('\n').Append(Details);
        }

        private static string GetHeading(AI.CliTraceEvent entry)
        {
            if (entry.Type == "notice")
                return entry.Payload;

            var title = entry.Type switch
            {
                "answer" => string.Empty,
                "thinking" or "reasoning" or "agent_think" => App.Text("DiffAnalysis.Thinking"),
                "tool_use" or "agent_tool_use" => App.Text("DiffAnalysis.ToolCall"),
                "tool_result" or "agent_tool_result" => App.Text("DiffAnalysis.ToolResult"),
                "result" => App.Text("DiffAnalysis.RunResult"),
                "error" => App.Text("DiffAnalysis.TraceError"),
                _ => entry.Type,
            };
            return string.IsNullOrEmpty(entry.Label) ? title : $"{title} · {entry.Label}";
        }

        private readonly string _type;
        private string _markdown;
        private bool _isExpanded;
    }
}
