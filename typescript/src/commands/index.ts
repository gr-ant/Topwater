import type { Shell } from '../shell.js';
import { registerBuiltins } from './builtins.js';
import { registerAwk } from './awk.js';
import { registerSed } from './sed.js';
import { registerBinary } from './binary.js';
import { registerJq } from './jq.js';
import { registerText } from './text.js';
import { registerFiles } from './files.js';
import { registerArchives } from './archive.js';
import { registerDocuments } from './documents.js';

/** Every built-in program, registered on a new shell. */
export function registerAll(shell: Shell) {
  registerBuiltins(shell);
  registerFiles(shell);
  registerText(shell);
  registerAwk(shell);
  registerJq(shell);
  registerSed(shell);
  registerBinary(shell);
  registerArchives(shell);
  registerDocuments(shell);
}
