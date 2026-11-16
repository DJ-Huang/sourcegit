using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    /// <summary>
    /// Reads the full staged diff of a repository. Used by CLI based commit message
    /// generators that cannot inspect files on their own.
    /// </summary>
    public class GetStagedDiffForAI : Command
    {
        public GetStagedDiffForAI(string repo, string amendParent)
        {
            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("diff --no-color --no-ext-diff --diff-algorithm=minimal --cached ");
            if (!string.IsNullOrEmpty(amendParent))
                builder.Append(amendParent).Append(' ');
            builder.Append("--");

            Args = builder.ToString();
        }

        public async Task<Result> ReadAsync()
        {
            return await ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
