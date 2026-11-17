using System.Collections.Generic;

namespace SourceGit.Models
{
    public class MrTemplate
    {
        public string Name
        {
            get; set;
        }

        public string Instruction
        {
            get; set;
        }

        public static List<MrTemplate> All { get; } =
        [
            new MrTemplate()
            {
                Name = "Simple",
                Instruction = "Write one short paragraph (2 to 3 sentences) that summarises what this merge request changes and why.",
            },
            new MrTemplate()
            {
                Name = "Detailed",
                Instruction = "Write the description with two sections: '## Changes' as a bullet list of the main changes, and '## Testing' as a bullet list of how it was verified. Keep each bullet to one line.",
            },
            new MrTemplate()
            {
                Name = "Checklist",
                Instruction = "Write the description with a short summary paragraph, then a '## Checklist' section using markdown checkboxes: builds locally, tests pass, docs updated, no breaking change. Mark the ones you can infer as checked and leave the rest unchecked.",
            },
        ];
    }
}
