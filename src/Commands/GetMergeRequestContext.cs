using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Collects the commits and the file change summary between a target branch and
    /// the current (source) branch. Used to build a merge request description.
    /// </summary>
    public class GetMergeRequestContext : Command
    {
        public GetMergeRequestContext(string repo, string source, string target)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("log --no-merges --stat ");
            builder.Append("--pretty=format:");
            builder.Append("\"=== %h %s%n%b\" ");
            builder.Append(target.Quoted()).Append("..").Append(source.Quoted());

            Args = builder.ToString();
        }

        public async Task<Result> ReadAsync()
        {
            return await ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
