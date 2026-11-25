// Schema-described tools (MCP servers, function-calling specs) exposed as one program with a subcommand per tool.
import type { Shell } from './shell.js';
import type { CommandContext } from './command.js';

/** A tool described by a name and a JSON Schema for its arguments (the shape MCP and function calling use). */
export interface ToolSpec { name: string; description?: string; inputSchema: unknown }
/** What a tool call returned. An error goes to stderr and makes the command exit 1. */
export interface ToolResult { text: string; isError?: boolean }
/** Calls a tool by its original name with the arguments parsed from the command line. */
export type ToolInvoker = (tool: string, args: Record<string, unknown>) => Promise<ToolResult> | ToolResult;

type Json = Record<string, unknown>;
interface Param { name: string; flag: string; type: string; itemType: string | null; required: boolean; schema: unknown; shape: unknown }

class UsageError extends Error {}
class HelpRequested extends Error {}

const isObj = (v: unknown): v is Json => typeof v === 'object' && v !== null && !Array.isArray(v);
const kebab = (s: string) => s.replace(/[_./ ]/g, '-');
const firstLine = (s?: string) => (s ?? '').trim().split('\n')[0].trim();

/** Follows a local $ref and unwraps "T or null" (how Pydantic writes Optional[T]) to T's schema. */
function resolve(schema: unknown, root: unknown, depth = 0): unknown {
  if (!isObj(schema) || depth > 8) return schema;
  const ref = schema.$ref;
  if (typeof ref === 'string' && ref.startsWith('#/')) {
    let target: unknown = root;
    for (const seg of ref.slice(2).split('/')) {
      if (!isObj(target)) return schema;
      target = target[seg.replaceAll('~1', '/').replaceAll('~0', '~')];
    }
    return target === undefined ? schema : resolve(target, root, depth + 1);
  }
  for (const key of ['anyOf', 'oneOf']) {
    const options = schema[key];
    if (Array.isArray(options)) {
      const real = options.map(o => resolve(o, root, depth + 1)).filter(o => typeOf(o) !== 'null');
      if (real.length === 1) return real[0];
    }
  }
  return schema;
}

/** The JSON Schema type of a resolved property; "" when it is not stated or is a union of several types. */
function typeOf(schema: unknown): string {
  if (!isObj(schema)) return '';
  const t = schema.type;
  if (typeof t === 'string') return t;
  if (Array.isArray(t)) {
    // ["string","null"] is a string; ["integer","string"] accepts either, so the value is not type-checked here
    const real = [...new Set(t.filter((x): x is string => typeof x === 'string' && x !== 'null'))];
    return real.length === 1 ? real[0] : '';
  }
  return Array.isArray(schema.enum) && schema.enum.every(x => typeof x === 'string') ? 'string' : '';
}

function params(tool: ToolSpec): Param[] {
  const s = tool.inputSchema;
  if (!isObj(s) || !isObj(s.properties)) return [];
  const required = new Set(Array.isArray(s.required) ? s.required.filter(r => typeof r === 'string') : []);
  return Object.entries(s.properties).map(([name, schema]) => {
    const shape = resolve(schema, s);
    const type = typeOf(shape);
    const itemType = type === 'array' && isObj(shape) && shape.items !== undefined ? typeOf(resolve(shape.items, s)) : null;
    return { name, flag: '--' + kebab(name), type, itemType, required: required.has(name), schema, shape };
  });
}

const placeholder = (p: Param) =>
  ({ integer: 'INT', number: 'NUM', object: 'JSON', string: 'TEXT', boolean: 'BOOL' } as Record<string, string>)[(p.type === 'array' ? p.itemType : p.type) ?? ''] ?? 'VALUE';

function parseBool(s: string, flag: string): boolean {
  const v = s.toLowerCase();
  if (v === 'true' || v === '1' || v === 'yes') return true;
  if (v === 'false' || v === '0' || v === 'no') return false;
  throw new UsageError(`option ${flag} expects true or false, got '${s}'`);
}

function tryJson(raw: string): { ok: boolean; value?: unknown } {
  try { return { ok: true, value: JSON.parse(raw) }; } catch { return { ok: false }; }
}

function convert(raw: string, type: string, flag: string): unknown {
  switch (type) {
    case 'string': return raw;
    case 'integer':
      if (/^[-+]?\d+$/.test(raw.trim())) return Number(raw);
      throw new UsageError(`option ${flag} expects a whole number, got '${raw}'`);
    case 'number': {
      const d = Number(raw);
      if (raw.trim() !== '' && Number.isFinite(d)) return d;
      throw new UsageError(`option ${flag} expects a number, got '${raw}'`);
    }
    case 'boolean': return parseBool(raw, flag);
    case 'object': {
      const j = tryJson(raw);
      if (j.ok && isObj(j.value)) return j.value;
      throw new UsageError(`option ${flag} expects a JSON object, got '${raw}'`);
    }
    default: {
      // no declared type: JSON if it looks like JSON (numbers, true, objects, arrays), otherwise text
      if (raw.length > 0 && (/^[\d{["-]/.test(raw) || ['true', 'false', 'null'].includes(raw))) { const j = tryJson(raw); if (j.ok) return j.value; }
      return raw;
    }
  }
}

class ToolGroup {
  constructor(private program: string, private description: string | undefined, private tools: ToolSpec[], private call: ToolInvoker) {}

  async run(ctx: CommandContext): Promise<number> {
    const args = ctx.args;
    if (args.length === 0 || ['--help', '-h', 'help'].includes(args[0])) { ctx.stdout.write(this.groupHelp()); return 0; }
    const tool = this.tools.find(t => t.name === args[0]) ?? this.tools.find(t => kebab(t.name) === kebab(args[0]));
    if (!tool) {
      ctx.stderr.write(`${this.program}: unknown tool '${args[0]}'. Run \`${this.program} --help\` to list tools.\n`);
      return 2;
    }
    const ps = params(tool);
    let callArgs: Json;
    try { callArgs = this.parse(ps, args.slice(1), ctx); } catch (e) {
      if (e instanceof HelpRequested) { ctx.stdout.write(this.toolHelp(tool, ps)); return 0; }
      if (e instanceof UsageError) {
        ctx.stderr.write(`${this.program} ${kebab(tool.name)}: ${e.message}\nTry \`${this.program} ${kebab(tool.name)} --help\`.\n`);
        return 2;
      }
      throw e;
    }
    const result = await this.call(tool.name, callArgs);
    const text = result.text.length === 0 || result.text.endsWith('\n') ? result.text : result.text + '\n';
    if (!result.isError) { ctx.stdout.write(text); return 0; }
    ctx.stderr.write(text.length === 0 ? `${this.program} ${kebab(tool.name)}: the tool reported an error\n` : text);
    return 1;
  }

  private groupHelp(): string {
    let s = this.description?.trim() ? this.description.trim() + '\n\n' : '';
    s += `Usage: ${this.program} TOOL [--option value]...\n       ${this.program} TOOL --help\n\nTools:\n`;
    const width = this.tools.length === 0 ? 0 : Math.min(32, Math.max(...this.tools.map(t => kebab(t.name).length)));
    for (const t of this.tools) s += `  ${kebab(t.name).padEnd(width)}  ${firstLine(t.description)}\n`;
    return s;
  }

  private toolHelp(tool: ToolSpec, ps: Param[]): string {
    const usage = ps.filter(p => p.required).map(p => (p.type === 'boolean' ? p.flag : `${p.flag} ${placeholder(p)}`)).join(' ');
    let s = `Usage: ${this.program} ${kebab(tool.name)}${usage ? ' ' + usage : ''}${ps.some(p => !p.required) ? ' [options]' : ''}\n`;
    if (tool.description?.trim()) s += '\n' + tool.description.trim() + '\n';
    if (ps.length > 0) {
      s += '\nOptions:\n';
      const left = ps.map(p => (p.type === 'boolean' ? p.flag : `${p.flag} ${placeholder(p)}`));
      const width = Math.min(34, Math.max(...left.map(l => l.length)));
      ps.forEach((p, i) => {
        const notes: string[] = [];
        const desc = (isObj(p.schema) && typeof p.schema.description === 'string' ? p.schema.description : undefined)
          ?? (isObj(p.shape) && typeof p.shape.description === 'string' ? p.shape.description : undefined);
        if (desc) notes.push(desc.trim().replace(/\r?\n/g, ' '));
        if (p.required) notes.push('(required)');
        if (p.type === 'array') notes.push('(repeatable)');
        if (isObj(p.shape) && Array.isArray(p.shape.enum)) notes.push('(one of: ' + p.shape.enum.map(x => (typeof x === 'string' ? x : JSON.stringify(x))).join(', ') + ')');
        if (isObj(p.schema) && 'default' in p.schema) notes.push(`(default: ${typeof p.schema.default === 'string' ? p.schema.default : JSON.stringify(p.schema.default)})`);
        s += `  ${left[i].padEnd(width)}  ${notes.join(' ')}\n`;
      });
    }
    s += '\n  --json JSON'.padEnd(37) + "pass all arguments as one JSON object ('-' reads it from stdin)\n";
    return s;
  }

  private parse(ps: Param[], args: string[], ctx: CommandContext): Json {
    // --json gives the base object and named options override it, whichever comes first on the command line
    const call: Json = {}, fromJson: Json = {};
    for (let i = 0; i < args.length; i++) {
      const arg = args[i];
      // only in option position, so `--title -h` passes "-h" as the title
      if ((arg === '--help' || arg === '-h') && !ps.some(x => x.flag === '--help')) throw new HelpRequested();
      if (!arg.startsWith('--') || arg.length === 2) throw new UsageError(`unexpected argument '${arg}'; arguments are passed as --name value`);
      let flag = arg, inline = '', hasInline = false;
      const eq = arg.indexOf('=');
      if (eq > 0) { flag = arg.slice(0, eq); inline = arg.slice(eq + 1); hasInline = true; }
      const next = () => {
        if (hasInline) return inline;
        if (++i < args.length) return args[i];
        throw new UsageError(`option ${flag} needs a value`);
      };
      if (flag === '--json' && !ps.some(x => x.flag === '--json')) {
        let raw = next();
        if (raw === '-') raw = ctx.stdinText;
        let node: unknown;
        try { node = JSON.parse(raw); } catch (e) { throw new UsageError(`--json is not valid JSON: ${(e as Error).message}`); }
        if (!isObj(node)) throw new UsageError('--json must be a JSON object');
        Object.assign(fromJson, node);
        continue;
      }
      let p = ps.find(x => x.flag === flag || '--' + x.name === flag);
      let negated = false;
      if (!p && flag.startsWith('--no-')) {
        p = ps.find(x => x.type === 'boolean' && (x.flag === '--' + flag.slice(5) || x.name === flag.slice(5)));
        negated = p !== undefined;
      }
      if (!p) {
        const known = ps.length === 0 ? 'this tool takes no options' : 'options are ' + ps.map(x => x.flag).join(', ');
        throw new UsageError(`unknown option ${flag}; ${known}`);
      }
      if (p.type === 'boolean') {
        let value = !negated;
        // a following true/false is taken as the value, so `--flag false` works as well as `--no-flag`
        if (hasInline) value = parseBool(inline, flag) !== negated;
        else if (!negated && (args[i + 1] === 'true' || args[i + 1] === 'false')) value = args[++i] === 'true';
        call[p.name] = value;
      } else if (p.type === 'array') {
        const raw = next();
        const array = Array.isArray(call[p.name]) ? (call[p.name] as unknown[]) : [];
        const whole = raw.trimStart().startsWith('[') ? tryJson(raw) : { ok: false };
        if (whole.ok && Array.isArray(whole.value)) array.push(...whole.value);
        else array.push(convert(raw, p.itemType ?? '', flag));
        call[p.name] = array;
      } else call[p.name] = convert(next(), p.type, flag);
    }
    // named options replace what --json set (a key keeps its place)
    const merged: Json = Object.assign({ ...fromJson }, call);
    const missing = ps.filter(p => p.required && !(p.name in merged)).map(p => p.flag);
    if (missing.length > 0) throw new UsageError('missing required option' + (missing.length > 1 ? 's ' : ' ') + missing.join(', '));
    return merged;
  }
}

/**
 * Exposes schema-described tools as one program with a subcommand per tool: `program` lists them,
 * `program TOOL --help` documents one, and `program TOOL --some-arg value` calls it.
 */
export function registerTools(shell: Shell, program: string, tools: ToolSpec[], call: ToolInvoker, description?: string): Shell {
  const list = [...tools].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const group = new ToolGroup(program, description, list, call);
  return shell.register(program, ctx => group.run(ctx), description ?? `${list.length} tools; run \`${program} --help\``, `${program} TOOL [--option value]...`);
}

// ---------- MCP ----------

/** The part of an MCP client this needs: the official @modelcontextprotocol/sdk `Client` fits as is. */
export interface McpClientLike {
  listTools(params?: unknown): Promise<{ tools: { name: string; description?: string; inputSchema: unknown }[] }>;
  callTool(params: { name: string; arguments?: Record<string, unknown> }): Promise<unknown>;
  getServerVersion?(): { name?: string; title?: string; description?: string } | undefined;
}

export interface McpMountOptions {
  /** Shown in `help` and at the top of `program --help`. */
  description?: string;
  /** Return false to hide a tool from the agent (by its MCP name). */
  include?: (tool: string) => boolean;
}

interface ContentBlock { type: string; text?: string; mimeType?: string; uri?: string; resource?: { uri?: string; mimeType?: string; text?: string } }

function render(result: { content?: ContentBlock[]; structuredContent?: unknown }): string {
  const parts = (result.content ?? []).map(b => {
    switch (b.type) {
      case 'text': return b.text ?? '';
      case 'image': return `[image, ${b.mimeType}]`;
      case 'audio': return `[audio, ${b.mimeType}]`;
      case 'resource_link': return `[resource ${b.uri}${b.mimeType ? ', ' + b.mimeType : ''}]`;
      case 'resource': return b.resource?.text ?? `[resource ${b.resource?.uri}${b.resource?.mimeType ? ', ' + b.resource.mimeType : ''}]`;
      default: return `[${b.type} content]`;
    }
  });
  if (parts.length === 0 && result.structuredContent !== undefined) parts.push(JSON.stringify(result.structuredContent));
  return parts.join('\n');
}

/**
 * Mounts a connected MCP server as one program: `program` lists its tools, `program TOOL --help` documents one,
 * and `program TOOL --arg value` calls it. The tool list is read once, now. The calls are real: they reach the server.
 * Returns the MCP names of the tools exposed.
 */
export async function mountMcp(shell: Shell, program: string, client: McpClientLike, options: McpMountOptions = {}): Promise<string[]> {
  const { tools } = await client.listTools();
  const specs = tools.filter(t => options.include?.(t.name) ?? true).map(t => ({ name: t.name, description: t.description, inputSchema: t.inputSchema }));
  const info = client.getServerVersion?.();
  const description = options.description ?? info?.description ?? info?.title ?? `${info?.name ?? program} tools`;
  registerTools(shell, program, specs, async (tool, args) => {
    try {
      const result = (await client.callTool({ name: tool, arguments: args })) as { content?: ContentBlock[]; structuredContent?: unknown; isError?: boolean };
      return { text: render(result), isError: result.isError === true };
    } catch (e) {
      // a server that failed or died is a failed command, not a reason to end the agent's run
      return { text: `the server reported an error: ${e instanceof Error ? e.message : String(e)}`, isError: true };
    }
  }, description);
  return specs.map(s => s.name);
}
