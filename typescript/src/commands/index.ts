import type { Shell } from '../shell.js';
import { registerScripting } from './scripting.js';
import { registerVariables } from './variables.js';
import { registerProcess } from './process.js';
import { registerSystem } from './system.js';
import { registerPaths } from './paths.js';
import { registerFiles } from './files.js';
import { registerText } from './text.js';
import { registerAwk } from './awk.js';
import { registerJq } from './jq.js';
import { registerBinary } from './binary.js';
import { registerArchives } from './archive.js';
import { registerDocuments } from './documents.js';
import { registerWeb } from './web.js';
import { registerGit } from './gitrevisions.js';
import { registerMount } from './mount.js';
import { registerPython } from './python.js';

/** Every built-in program, registered on a new shell. */
export function registerAll(shell: Shell) {
  registerScripting(shell);
  registerVariables(shell);
  registerProcess(shell);
  registerSystem(shell);
  registerPaths(shell);
  registerFiles(shell);
  registerText(shell);
  registerAwk(shell);
  registerJq(shell);
  registerBinary(shell);
  registerArchives(shell);
  registerDocuments(shell);
  registerWeb(shell);
  registerGit(shell);
  registerMount(shell);
  registerPython(shell);
}
