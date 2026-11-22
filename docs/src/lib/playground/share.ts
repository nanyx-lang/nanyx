// Share links: the code in the URL's fragment, `#code=` and the code compressed with raw deflate and written in
// base64url. A fragment isn't sent to the server, so the code isn't in its logs and has no length limit there. The
// docs build writes the same form for the tour's "Open in the playground" links (`shareFragment` in
// `site/playground-examples.mjs`).

async function through(bytes: Uint8Array, stream: CompressionStream | DecompressionStream) {
  const piped = new Blob([bytes as BlobPart]).stream().pipeThrough(stream as TransformStream<Uint8Array, Uint8Array>);
  return new Uint8Array(await new Response(piped).arrayBuffer());
}

function toBase64Url(bytes: Uint8Array) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function fromBase64Url(text: string) {
  const binary = atob(text.replace(/-/g, "+").replace(/_/g, "/"));
  return Uint8Array.from(binary, (ch) => ch.charCodeAt(0));
}

/** The link that opens `code` in the playground. */
export async function shareLink(code: string) {
  const compressed = await through(new TextEncoder().encode(code), new CompressionStream("deflate-raw"));
  return `${location.origin}/playground#code=${toBase64Url(compressed)}`;
}

/**
 * The code a share link's fragment holds: `null` when `hash` isn't a share link's, and `{ error }` when it is but its
 * code doesn't decode.
 */
export async function codeOf(hash: string): Promise<string | null | { error: string }> {
  const match = /^#?code=(.*)$/.exec(hash);
  if (!match) return null;
  try {
    const bytes = await through(fromBase64Url(match[1]), new DecompressionStream("deflate-raw"));
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch (error) {
    return { error: String(error) };
  }
}
