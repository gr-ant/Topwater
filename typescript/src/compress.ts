// Compression through the web-standard CompressionStream (Node 22 and browsers).
// shortcut: CompressionStream has no level setting; levels are accepted but ignored, vendor a deflate if exact levels matter
export async function deflate(kind: 'gzip' | 'deflate-raw', data: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([data as never]).stream().pipeThrough(new CompressionStream(kind));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}
