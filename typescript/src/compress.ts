// Compression through the web-standard CompressionStream (Node 22 and browsers).
// shortcut: CompressionStream has no level setting; levels are accepted but ignored, vendor a deflate if exact levels matter
export async function deflate(kind: 'gzip' | 'deflate-raw', data: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([data as never]).stream().pipeThrough(new CompressionStream(kind));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/** Raw DEFLATE decoded natively (much faster than the inflate.ts decoder), failing once the output passes `max` bytes. */
export async function inflateRawNative(data: Uint8Array, max: number): Promise<Uint8Array> {
  const reader = new Blob([data as never]).stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  for (let r = await reader.read(); !r.done; r = await reader.read()) {
    if ((size += r.value.length) > max) { await reader.cancel(); throw new Error('output limit exceeded'); }
    chunks.push(r.value);
  }
  const out = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) { out.set(c, at); at += c.length; }
  return out;
}
