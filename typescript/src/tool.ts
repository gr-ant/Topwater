import type { Shell } from './shell.js';

const inputSchema = {
  type: 'object',
  properties: { command: { type: 'string', description: 'The bash command to run.' } },
  required: ['command'],
} as const;

/** The single tool definition to give the model. Wire its "command" input to `Shell.run`. */
export const BashTool = {
  name: 'bash',
  description:
    'Run a command in a sandboxed Linux shell (bash). The working directory, environment variables and files persist between calls. ' +
    'Standard command-line tools are available (ls, cat, grep, sed, awk, jq, find, sort, diff, git, ...); git works on local repositories. ' +
    'Python, network access and package installation are not. ' +
    'Read Word, Excel, PowerPoint, PDF and HTML files with `to-markdown FILE`. ' +
    "Other programs on this machine are live interfaces to this application's systems, so their actions are real: " +
    'run `help` to list them and `<program> --help` for usage.',
  inputSchema,
  inputSchemaJson: JSON.stringify(inputSchema, null, 2),
  /** The description for `shell`: with a web policy (allowWeb), it names the sites `curl` and `wget` reach instead of
   * saying there is no network. Every adapter uses it when you give no description. */
  descriptionFor(shell: Shell): string {
    const web = shell.web;
    if (!web || web.isReplay || web.policy.domains.length === 0) return BashTool.description;
    const { methods } = web.policy, domains = web.policy.domains.map(d => d.trim());
    const sites = domains.slice(0, 20).join(', ') + (domains.length > 20 ? `, and ${domains.length - 20} more` : '');
    return BashTool.description.replace('Python, network access and package installation are not. ',
      `Python and package installation are not; \`curl\` and \`wget\` reach only these sites (methods: ${methods.map(m => m.toUpperCase()).join(', ')}): ${sites}. `);
  },

  async execute(shell: Shell, command: string): Promise<string> { return (await shell.run(command)).toToolOutput(); },
};
