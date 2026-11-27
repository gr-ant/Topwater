// to-markdown: the command; the converters are in ../convert.ts. See docs/architecture.md.
import { Args, specOf, specMessage, ShellError, reason, type CommandContext } from '../command.js';
import type { Shell } from '../shell.js';
import { toMarkdown } from '../convert.js';

function run(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('to-markdown'));
  if (a.positional.length === 0) throw new ShellError(specMessage('to-markdown', 'missingOperand', {}));
  let code = 0;
  const parts: string[] = [];
  for (const f of a.positional) {
    let md: string;
    try { md = toMarkdown(ctx.fs.readBytes(ctx.resolvePath(f)), f); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      // filesystem errors carry the path ("/abs/x: No such file..."); show the name as typed instead
      ctx.stderr.write(`${ctx.name}: ${specMessage('to-markdown', 'convertFailed', { file: f, reason: e.message.startsWith('/') ? reason(e) : e.message })}\n`);
      code = 1;
      continue;
    }
    if (md.length > 0 && !md.endsWith('\n')) md += '\n';
    parts.push(a.positional.length > 1 ? `# ${f}\n\n${md}` : md);
  }
  const text = parts.join('\n');
  const output = a.value('o');
  if (output !== undefined) ctx.fs.writeText(ctx.resolvePath(output), text); else ctx.stdout.write(text);
  return code;
}

export function registerDocuments(shell: Shell): void {
  shell.register('to-markdown', run);
}

export { converterLimits, toMarkdown, addDocument } from '../convert.js';
