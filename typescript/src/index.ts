export { Shell, ShellResult, type ShellOptions } from './shell.js';
export { VirtualFileSystem, ShellError, type FileStat } from './vfs.js';
export { type CommandContext, type CommandHandler, type Writer, Args, inputs, inputBytes, splitLines } from './command.js';
export { BashTool } from './tool.js';
export { registerTools, mountMcp, type ToolSpec, type ToolResult, type ToolInvoker, type McpClientLike, type McpMountOptions } from './tools.js';
export { registerApi, type ApiEndpoint } from './api.js';
export { toMarkdown, addDocument } from './commands/documents.js';
