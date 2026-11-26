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
  /** Runs the model's command and returns the text to send back as the tool result. */
  async execute(shell: Shell, command: string): Promise<string> { return (await shell.run(command)).toToolOutput(); },
};
